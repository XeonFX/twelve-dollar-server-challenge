// A load generator for the $12 server challenge that replays bench/load.js with a few KB per user instead of k6's
// megabytes, so tens of thousands of users fit on a small load box.
//
//   users     N virtual users, each on its own keep-alive connection, looping exactly as load.js does:
//             GET /feed, think 3-7 s, GET /posts/:id (one from the feed), think 3-8 s, like it 15% of the time,
//             write a post 2% of the time, idle 5-15 s. Ramp, hold, then report the hold's latencies.
//   saturate  C connections running the same loop with no think time: the most requests per second the server does.
//   steps     the users loop, ramped at --rate users/s to each of --levels in turn; at each level it settles for
//             --settle s, prints "HOLD <users>", holds --hold s, prints the hold's latencies and "DONE <users>". A
//             script watching stdout measures the server between the two lines.
//
// --sources K binds the users' sockets to 127.0.0.2 .. 127.0.0.(K+1), and --sources A.B.C.D/K to A.B.C.D and the
// K-1 addresses after it (a VM's alias IP range), with the port picked at connect (IP_BIND_ADDRESS_NO_PORT), so more
// than one address's ephemeral ports reach one server port.
//
// dotnet run -c Release -- users --url http://srv:80 --tokens tokens.json --users 20000 --ramp 60 --hold 120
// dotnet run -c Release -- saturate --url http://srv:80 --tokens tokens.json --conns 256 --hold 30
// dotnet run -c Release -- steps --url http://127.0.0.1:8080 --tokens tokens.json --levels 10000,50000 --sources 8

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

var opts = new Dictionary<string, string>();
for (int i = 1; i + 1 < args.Length; i += 2) opts[args[i].TrimStart('-')] = args[i + 1];
string mode = args.Length > 0 ? args[0] : "users";
var url = new Uri(opts.GetValueOrDefault("url", "http://127.0.0.1:3000"));
var tokens = Tokens.Load(opts.GetValueOrDefault("tokens", "tokens.json"));
int hold = int.Parse(opts.GetValueOrDefault("hold", "60"));
int ramp = int.Parse(opts.GetValueOrDefault("ramp", "30"));
var endPoint = new IPEndPoint((await Dns.GetHostAddressesAsync(url.Host))[0], url.Port);
var stats = new Stats();
using var stop = new CancellationTokenSource();
Client.Sources = Client.ParseSources(opts.GetValueOrDefault("sources", "0"));

if (mode == "steps")
{
    int[] levels = Array.ConvertAll(opts.GetValueOrDefault("levels", "10000,50000,100000,150000").Split(','), int.Parse);
    double rate = double.Parse(opts.GetValueOrDefault("rate", "1000"), System.Globalization.CultureInfo.InvariantCulture);
    int settle = int.Parse(opts.GetValueOrDefault("settle", "25"));
    var all = new List<Task>(levels[^1]);
    foreach (int level in levels)
    {
        var watch = Stopwatch.StartNew();
        int from = all.Count;
        for (int i = from; i < level; i++)
        {
            double wait = (i - from) / rate - watch.Elapsed.TotalSeconds;
            if (wait > 0.005) await Task.Delay(TimeSpan.FromSeconds(wait));
            all.Add(Vu.Run(endPoint, url.Host, tokens[i % tokens.Length], stats, stop.Token, think: true, user: i));
        }
        await Task.Delay(TimeSpan.FromSeconds(settle));
        Console.WriteLine($"HOLD {level} open={Volatile.Read(ref Client.Open)} failedConnects={Volatile.Read(ref Client.FailedConnects)}");
        stats.Start();
        await Task.Delay(TimeSpan.FromSeconds(hold));
        Console.Write(stats.Stop().Format($"users={level} rate={rate}/s settle={settle}s hold={hold}s"));
        Console.WriteLine($"DONE {level} open={Volatile.Read(ref Client.Open)} failedConnects={Volatile.Read(ref Client.FailedConnects)}");
        stats.Reset();
    }
    stop.Cancel();
    await Task.WhenAny(Task.WhenAll(all), Task.Delay(5000));
}
else if (mode == "saturate")
{
    int conns = int.Parse(opts.GetValueOrDefault("conns", "256"));
    var all = new Task[conns];
    string only = opts.GetValueOrDefault("only", "");
    for (int i = 0; i < conns; i++) all[i] = only.Length > 0 ? Vu.Only(endPoint, url.Host, tokens[i % tokens.Length], stats, stop.Token, only) : Vu.Run(endPoint, url.Host, tokens[i % tokens.Length], stats, stop.Token, think: false);
    await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, hold / 3.0)));   // warm-up, not counted
    stats.Start();
    await Task.Delay(TimeSpan.FromSeconds(hold));
    var report = stats.Stop();
    stop.Cancel();
    await Task.WhenAny(Task.WhenAll(all), Task.Delay(5000));
    Console.WriteLine(report.Format($"saturate conns={conns} hold={hold}s"));
}
else
{
    int users = int.Parse(opts.GetValueOrDefault("users", "1000"));
    var all = new List<Task>(users);
    var rampWatch = Stopwatch.StartNew();
    for (int i = 0; i < users; i++)
    {
        // linear ramp, as k6's ramping-vus
        double due = ramp * (double)i / users;
        double wait = due - rampWatch.Elapsed.TotalSeconds;
        if (wait > 0.005) await Task.Delay(TimeSpan.FromSeconds(wait));
        all.Add(Vu.Run(endPoint, url.Host, tokens[i % tokens.Length], stats, stop.Token, think: true, user: i));
    }
    stats.Start();
    await Task.Delay(TimeSpan.FromSeconds(hold));
    var report = stats.Stop();
    stop.Cancel();
    await Task.WhenAny(Task.WhenAll(all), Task.Delay(5000));
    Console.WriteLine(report.Format($"users={users} ramp={ramp}s hold={hold}s"));
}

sealed record Token(string Username, string Bearer);

static class Tokens
{
    public static Token[] Load(string path)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllBytes(path));
        var list = new List<Token>();
        foreach (var t in doc.RootElement.EnumerateArray())
            list.Add(new Token(t.GetProperty("username").GetString()!, t.GetProperty("token").GetString()!));
        return list.ToArray();
    }
}

static class Vu
{
    static double Between(double lo, double hi) => lo + Random.Shared.NextDouble() * (hi - lo);

    public static async Task Run(IPEndPoint ep, string host, Token token, Stats stats, CancellationToken stop, bool think, int user = 0)
    {
        var c = new Client(ep, host, token, user);
        try
        {
            if (think) await Delay(Between(0, 5), stop);
            int iter = 0;
            while (!stop.IsCancellationRequested)
            {
                var feed = await c.Send(Endpoint.Feed, 0, iter, stats);
                if (!feed.Ok)
                {
                    if (think) await Delay(Between(3, 7), stop);
                    iter++;
                    continue;
                }
                long post = c.PickPostId();
                if (think) await Delay(Between(3, 7), stop);
                await c.Send(Endpoint.Post, post, iter, stats);
                if (think) await Delay(Between(3, 8), stop);
                if (Random.Shared.NextDouble() < 0.15) await c.Send(Endpoint.Like, post, iter, stats);
                if (Random.Shared.NextDouble() < 0.02) await c.Send(Endpoint.Create, 0, iter, stats);
                if (think) await Delay(Between(5, 15), stop);
                iter++;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            c.Dispose();
        }
    }

    /// <summary>One endpoint over and over: health, feed, post (a random id), like, create.</summary>
    public static async Task Only(IPEndPoint ep, string host, Token token, Stats stats, CancellationToken stop, string what)
    {
        var c = new Client(ep, host, token);
        try
        {
            int iter = 0;
            while (!stop.IsCancellationRequested)
            {
                long id = Random.Shared.Next(1, 500001);
                switch (what)
                {
                    case "health": await c.Send(Endpoint.Health, 0, iter, stats); break;
                    case "feed": await c.Send(Endpoint.Feed, 0, iter, stats); break;
                    case "post": await c.Send(Endpoint.Post, id, iter, stats); break;
                    case "like": await c.Send(Endpoint.Like, id, iter, stats); break;
                    default: await c.Send(Endpoint.Create, 0, iter, stats); break;
                }
                iter++;
            }
        }
        finally
        {
            c.Dispose();
        }
    }

    static Task Delay(double seconds, CancellationToken stop) => Task.Delay(TimeSpan.FromSeconds(seconds), stop);
}

enum Endpoint { Feed, Post, Like, Create, Health }

/// <summary>One user's keep-alive connection. Its buffers are rented for a request and returned after it, so a user
/// thinking holds only the socket, and tens of thousands of them fit on the load box.</summary>
sealed class Client(IPEndPoint ep, string host, Token token, int user = 0) : IDisposable
{
    public static IPAddress[] Sources = [];

    public static IPAddress[] ParseSources(string s)
    {
        int slash = s.IndexOf('/');
        if (slash < 0)
        {
            int k = int.Parse(s);
            return Enumerable.Range(0, k).Select(i => new IPAddress([127, 0, 0, (byte)(2 + i)])).ToArray();
        }
        byte[] b = IPAddress.Parse(s[..slash]).GetAddressBytes();
        uint start = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        return Enumerable.Range(0, int.Parse(s[(slash + 1)..])).Select(i =>
        {
            uint a = start + (uint)i;
            return new IPAddress([(byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a]);
        }).ToArray();
    }
    public static int Open, FailedConnects;
    Socket? _socket;
    byte[] _buf = [];
    int _bodyStart, _bodyLength;
    byte[] _req = [];
    readonly long[] _ids = new long[20];
    int _idCount;

    public readonly record struct Result(bool Ok);

    public async Task<Result> Send(Endpoint e, long id, int iter, Stats stats)
    {
        _req = ArrayPool<byte>.Shared.Rent(2048);
        int status;
        long start;
        try
        {
            int n = Build(e, id, iter);
            start = Stopwatch.GetTimestamp();
            try
            {
                if (_socket is null)
                {
                    var s = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        if (Sources.Length > 0)
                        {
                            s.SetRawSocketOption(0 /* SOL_IP */, 24 /* IP_BIND_ADDRESS_NO_PORT */, [1, 0, 0, 0]);
                            s.Bind(new IPEndPoint(Sources[user % Sources.Length], 0));
                        }
                        await s.ConnectAsync(ep);
                    }
                    catch
                    {
                        Interlocked.Increment(ref FailedConnects);
                        s.Dispose();
                        throw;
                    }
                    _socket = s;
                    Interlocked.Increment(ref Open);
                }
                await _socket.SendAsync(_req.AsMemory(0, n), SocketFlags.None);
                ArrayPool<byte>.Shared.Return(_req);
                _req = [];
                status = await Receive();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or System.IO.IOException or InvalidOperationException)
            {
                Close();
                status = 0;
            }
        }
        finally
        {
            if (_req.Length > 0) ArrayPool<byte>.Shared.Return(_req);
            _req = [];
        }
        bool ok = e switch
        {
            Endpoint.Feed or Endpoint.Post or Endpoint.Health => status == 200,
            Endpoint.Like => status is 200 or 201,
            _ => status == 201,
        };
        if (ok && e == Endpoint.Feed)
        {
            // a feed without posts is a failed request: the user has no post to read, and the load would not be
            // the one the challenge runs
            _idCount = ResponseHead.PostIds(_buf.AsSpan(_bodyStart, _bodyLength), _ids);
            ok = _idCount > 0;
        }
        stats.Record(e, Stopwatch.GetElapsedTime(start), ok);
        if (_buf.Length > 0) ArrayPool<byte>.Shared.Return(_buf);
        _buf = [];
        return new Result(ok);
    }

    int Build(Endpoint e, long id, int iter)
    {
        var sb = new StringBuilder(512);
        switch (e)
        {
            case Endpoint.Feed: sb.Append("GET /feed HTTP/1.1\r\n"); break;
            case Endpoint.Health: sb.Append("GET /health HTTP/1.1\r\n"); break;
            case Endpoint.Post: sb.Append("GET /posts/").Append(id).Append(" HTTP/1.1\r\n"); break;
            case Endpoint.Like: sb.Append("POST /posts/").Append(id).Append("/like HTTP/1.1\r\nAuthorization: Bearer ").Append(token.Bearer).Append("\r\nContent-Length: 0\r\n"); break;
            default:
                string body = $"{{\"body\":\"{token.Username} says hi at {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} (iter {iter})\"}}";
                sb.Append("POST /posts HTTP/1.1\r\nHost: ").Append(host).Append("\r\nAuthorization: Bearer ").Append(token.Bearer)
                  .Append("\r\nContent-Type: application/json\r\nContent-Length: ").Append(Encoding.UTF8.GetByteCount(body)).Append("\r\n\r\n").Append(body);
                return Encoding.UTF8.GetBytes(sb.ToString(), _req);
        }
        sb.Append("Host: ").Append(host).Append("\r\n\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString(), _req);
    }

    /// <summary>Waits for the response's first bytes with a zero-byte receive before it rents the buffer, so the
    /// requests a slow server holds cost the load generator no buffers.</summary>
    async Task<int> Receive()
    {
        await _socket!.ReceiveAsync(Memory<byte>.Empty, SocketFlags.None);
        _buf = ArrayPool<byte>.Shared.Rent(8 << 10);
        int have = 0;
        while (true)
        {
            if (have == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
            int got = await _socket!.ReceiveAsync(_buf.AsMemory(have), SocketFlags.None);
            if (got == 0) throw new System.IO.IOException("closed");
            have += got;
            var span = _buf.AsSpan(0, have);
            int headEnd = span.IndexOf("\r\n\r\n"u8);
            if (headEnd < 0) continue;
            if (!ResponseHead.TryParse(span[..headEnd], out int status, out int length)) throw new System.IO.IOException("not a response");
            if (have < headEnd + 4 + length) continue;
            _bodyStart = headEnd + 4;
            _bodyLength = length;
            return status;
        }
    }

    /// <summary>A post of the feed this user read last.</summary>
    public long PickPostId() => _ids[Random.Shared.Next(_idCount)];

    void Close()
    {
        if (_socket is null) return;
        _socket.Dispose();
        _socket = null;
        Interlocked.Decrement(ref Open);
    }

    public void Dispose() => Close();
}

/// <summary>Latencies in 10 µs buckets up to 10 s, per endpoint, while recording is on.</summary>
sealed class Stats
{
    const int Buckets = 1_000_000;
    readonly long[][] _hist = [new long[Buckets], new long[Buckets], new long[Buckets], new long[Buckets], new long[Buckets]];
    readonly long[] _errors = new long[5];
    volatile bool _on;
    long _startTs;
    double _seconds;

    public void Start()
    {
        _startTs = Stopwatch.GetTimestamp();
        _on = true;
    }

    public void Record(Endpoint e, TimeSpan t, bool ok)
    {
        if (!_on) return;
        int b = (int)Math.Min(Buckets - 1, t.Ticks / 100);
        Interlocked.Increment(ref _hist[(int)e][b]);
        if (!ok) Interlocked.Increment(ref _errors[(int)e]);
    }

    public void Reset()
    {
        foreach (long[] h in _hist) Array.Clear(h);
        Array.Clear(_errors);
    }

    public Stats Stop()
    {
        _on = false;
        _seconds = Stopwatch.GetElapsedTime(_startTs).TotalSeconds;
        return this;
    }

    public string Format(string title)
    {
        var sb = new StringBuilder();
        var total = new long[Buckets];
        long totalErrors = 0;
        sb.AppendLine(title);
        string[] names = ["GET /feed", "GET /posts/:id", "POST like", "POST /posts", "GET /health"];
        for (int e = 0; e < 5; e++)
        {
            for (int b = 0; b < Buckets; b++) total[b] += _hist[e][b];
            totalErrors += _errors[e];
            sb.AppendLine(Line(names[e], _hist[e], _errors[e]));
        }
        sb.AppendLine(Line("all", total, totalErrors));
        return sb.ToString();
    }

    string Line(string name, long[] h, long errors)
    {
        long n = 0;
        foreach (long c in h) n += c;
        if (n == 0) return $"{name,-15} none";
        double P(double q)
        {
            long target = (long)Math.Ceiling(q * n), seen = 0;
            for (int b = 0; b < h.Length; b++)
                if ((seen += h[b]) >= target) return (b + 1) / 100.0; // ms
            return double.NaN;
        }
        return $"{name,-15} {n,10:N0} req {n / _seconds,10:N0} req/s  p50 {P(0.5),8:F2} ms  p95 {P(0.95),8:F2} ms  p99 {P(0.99),8:F2} ms  max {P(1),8:F2} ms  errors {errors} ({100.0 * errors / n:F3}%)";
    }
}
