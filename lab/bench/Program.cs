using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

/// <summary>
/// The entry's pieces, one benchmark each, on a copy of the seed (SEED_DB, with tokens.json beside it). A request
/// benchmark runs passes as the loop does: 64 requests through the HTTP layer, then the commit; its result is per
/// request.
/// <para><see cref="Data"/> is what the database holds besides the seed. A piece that is fast on the seed and slow
/// on <see cref="Database.Load"/> is slow under the challenge's load.</para>
/// </summary>
[MemoryDiagnoser]
public unsafe class Pieces
{
    const int PerPass = 64, Variants = 4096;
    const string Head = " HTTP/1.1\r\nHost: 10.0.0.5\r\nUser-Agent: Grafana k6/1.3.0\r\n";

    public enum Database
    {
        /// <summary>The seed alone.</summary>
        Seed,
        /// <summary>After ten minutes of bench/load.js at 150,000 users: 40,000 new posts (66 a second), each
        /// with the 7 likes that 15 % of its readers give it. A reader likes a post 6 to 15 s after the feed
        /// showed it among the newest 20, and reads it 3 to 7 s after.</summary>
        Load,
        /// <summary>Every user of the seed has liked post 1, and every reader reads it: the most likes one post
        /// can have. (The lab's load generator sent every user there until it read the entry's Content-Length.)</summary>
        OnePost,
    }

    /// <summary>New posts a second at 150,000 users.</summary>
    const double PostsPerSecond = 66;

    [ParamsAllValues]
    public Database Data;

    static string? _dir;
    static string[] _tokens = [];
    static Database _have;
    static readonly Random Rng = new(3);
    static readonly Arena Out = new(1 << 22);
    static long _iteration;

    byte[][] _feed = [], _post = [], _like = [], _create = [], _missing = [], _mix = [];
    byte[] _body = [], _created = [], _author = [];

    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    static byte[] NewLike(long id) => LikeBy(id, _tokens[Rng.Next(_tokens.Length)]);

    static byte[] LikeBy(long id, string token) =>
        Bytes($"POST /posts/{id}/like{Head}Content-Length: 0\r\nAuthorization: Bearer {token}\r\nContent-Type: application/json\r\n\r\n");

    /// <summary>A post of the feed a user got <paramref name="from"/> to <paramref name="to"/> seconds ago: one of
    /// the 20 newest of that moment.</summary>
    static long Shown(long newest, double from, double to) =>
        Math.Max(1, newest - (long)(PostsPerSecond * (from + Rng.NextDouble() * (to - from))) - Rng.Next(20));

    static byte[] NewPost()
    {
        string json = $"{{\"body\":\"user says hi at 2026-11-12T05:00:00.000Z (VU {Rng.Next(100000)}, iter {Rng.Next(100)})\"}}";
        return Bytes($"POST /posts{Head}Content-Length: {json.Length}\r\nAuthorization: Bearer {_tokens[Rng.Next(_tokens.Length)]}\r\nContent-Type: application/json\r\n\r\n{json}");
    }

    /// <summary>One pass of the loop without its sockets: the checkpoint round's step, the requests, the commit.
    /// Writes wait while a checkpoint holds them, as they do in the server.</summary>
    static void Pass(byte[][] requests, ref long next)
    {
        Api.Tick();
        Db.CheckpointStep(Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000));
        while (Db.HoldWrites)
        {
            if (Db.InTxn) Db.Commit();
            Thread.Sleep(1);
            Db.CheckpointStep(Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000));
        }
        Db.WritesOpen = true;
        Out.Length = 0;
        for (int i = 0; i < PerPass; i++)
        {
            byte[] request = requests[next++ % requests.Length];
            int flags = 0;
            if (Http.Serve(request, Out, ref flags) != request.Length) throw new InvalidOperationException("request not served whole");
        }
        if (Db.InTxn && !Db.Commit()) throw new InvalidOperationException("commit failed");
    }

    [GlobalSetup]
    public void Setup()
    {
        if (_dir is null)
        {
            string seed = Environment.GetEnvironmentVariable("SEED_DB") ?? throw new InvalidOperationException("SEED_DB is not set");
            _dir = Directory.CreateTempSubdirectory("twelve-bench-").FullName;
            string path = Path.Combine(_dir, "feed.db");
            File.Copy(seed, path);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(_dir, true); } catch (IOException) { } };
            using JsonDocument tokens = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(seed)!, "tokens.json")));
            _tokens = new string[tokens.RootElement.GetArrayLength()];
            for (int i = 0; i < _tokens.Length; i++) _tokens[i] = tokens.RootElement[i].GetProperty("token").GetString()!;
            Db.Open(path, 262144);
            Db.Warm();
            Db.StartReads();
            if (!Reads.On) throw new InvalidOperationException("B-tree reads are off");
            Db.StartCheckpoints();
            Auth.Init("twelve-dollar-challenge");
        }
        if (Data < _have) throw new InvalidOperationException("the database only grows: run Seed, Load, OnePost in this order");
        var pass = new byte[PerPass][];
        long next = 0;
        long newest = Db.Scalar("SELECT max(id) FROM posts");
        if (Data >= Database.Load && _have < Database.Load)
        {
            for (int grown = 0; grown < 40_000;)
            {
                for (int i = 0; i < PerPass; i++)
                {
                    if (i % 8 == 0)
                    {
                        pass[i] = NewPost();
                        newest++;
                        grown++;
                    }
                    else pass[i] = NewLike(Shown(newest, 6, 15));
                }
                next = 0;
                Pass(pass, ref next);
            }
        }
        if (Data == Database.OnePost && _have < Database.OnePost)
        {
            for (int user = 0; user < _tokens.Length;)
            {
                for (int i = 0; i < PerPass; i++, user++) pass[i] = LikeBy(1, _tokens[user % _tokens.Length]);
                next = 0;
                Pass(pass, ref next);
            }
        }
        _have = Data;
        newest = Db.Scalar("SELECT max(id) FROM posts");
        bool onePost = Data == Database.OnePost;

        _feed = [Bytes($"GET /feed{Head}\r\n")];
        _missing = [Bytes($"GET /nothing{Head}\r\n")];
        _post = new byte[Variants][];
        _like = new byte[Variants][];
        _create = new byte[Variants][];
        _mix = new byte[Variants][];
        for (int i = 0; i < Variants; i++)
        {
            _post[i] = Bytes($"GET /posts/{(onePost ? 1 : Shown(newest, 3, 7))}{Head}\r\n");
            _like[i] = NewLike(onePost ? 1 : Shown(newest, 6, 15));
            _create[i] = NewPost();
            _mix[i] = (i % 50) switch { < 23 => _feed[0], < 46 => _post[i], < 49 => _like[i], _ => _create[i] };
        }
        _body = Bytes("user_00006 says hi at 2026-11-12T05:00:00.000Z (VU 6, iter 12)");
        _created = Bytes("2026-11-12T05:00:00.000Z");
        _author = Bytes("user_00006");
    }

    [Benchmark(OperationsPerInvoke = PerPass)]
    public void NotFound() => Pass(_missing, ref _iteration);

    [Benchmark(OperationsPerInvoke = PerPass)]
    public void Feed() => Pass(_feed, ref _iteration);

    [Benchmark(OperationsPerInvoke = PerPass)]
    public void Post() => Pass(_post, ref _iteration);

    [Benchmark(OperationsPerInvoke = PerPass)]
    public void Like() => Pass(_like, ref _iteration);

    [Benchmark(OperationsPerInvoke = PerPass)]
    public void Create() => Pass(_create, ref _iteration);

    /// <summary>The load test's mix: 46 % feeds, 46 % posts, 6 % likes, 2 % new posts.</summary>
    [Benchmark(OperationsPerInvoke = PerPass)]
    public void Mix() => Pass(_mix, ref _iteration);

    /// <summary>The feed's rows read and written as JSON, without the HTTP layer.</summary>
    [Benchmark(OperationsPerInvoke = PerPass)]
    public void FeedRead()
    {
        Db.Begin();
        for (int i = 0; i < PerPass; i++)
        {
            Json.Out = Out;
            Out.Length = 0;
            if (!Reads.Feed()) throw new InvalidOperationException("the feed fell back to SQL");
        }
        Db.Commit();
    }

    /// <summary>The 20 post objects of a feed as JSON, from memory.</summary>
    [Benchmark]
    public void FeedJson()
    {
        fixed (byte* b = _body)
        fixed (byte* c = _created)
        fixed (byte* a = _author)
        {
            Json.Out = Out;
            Out.Length = 0;
            for (int k = 0; k < 20; k++) Json.Post(500_000 - k, b, _body.Length, c, _created.Length, a, _author.Length, k % 3);
        }
    }
}

static class Program
{
    static void Main(string[] args) =>
        // one short job, in this process: the benchmarks share the database they grew
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, DefaultConfig.Instance
            .AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance))
            .WithOptions(ConfigOptions.DisableOptimizationsValidator));
}
