using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;
using BenchmarkDotNet.Toolchains.NativeAot;

/// <summary>
/// The entry's pieces, one benchmark each, on a copy of the seed (SEED_DB, with tokens.json beside it). A request
/// benchmark runs passes as the loop does: 64 requests through the HTTP layer, then the commit; its result is per
/// request.
/// <para><see cref="Grown"/> is the number of posts the database got first the way a load test gives them: each from
/// a random user, with likes of the newest posts between them (7 for each post, as 15 % of readers like and 2 %
/// create). A piece that is fast on the seed and slow on a grown database is slow under load.</para>
/// </summary>
[MemoryDiagnoser]
public unsafe class Pieces
{
    const int PerPass = 64, Variants = 4096;
    const string Head = " HTTP/1.1\r\nHost: 10.0.0.5\r\nUser-Agent: Grafana k6/1.3.0\r\n";

    [ParamsSource(nameof(Growths))]
    public int Grown;

    /// <summary>GROWN=0,5000 picks the sizes; by default all three.</summary>
    public static IEnumerable<int> Growths() =>
        (Environment.GetEnvironmentVariable("GROWN") is { Length: > 0 } v ? v : "0,5000,40000").Split(',').Select(int.Parse);

    static string? _dir;
    static string[] _tokens = [];
    static int _grown;
    static readonly Random Rng = new(3);
    static readonly Arena Out = new(1 << 22);
    static long _iteration;

    byte[][] _feed = [], _post = [], _like = [], _create = [], _missing = [], _mix = [];
    byte[] _body = [], _created = [], _author = [];

    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    static byte[] NewLike(long id) =>
        Bytes($"POST /posts/{id}/like{Head}Content-Length: 0\r\nAuthorization: Bearer {_tokens[Rng.Next(_tokens.Length)]}\r\nContent-Type: application/json\r\n\r\n");

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
        if (Grown < _grown) throw new InvalidOperationException("Grown must not go down: the database only grows");
        // grow: a post, then 7 likes of posts among the 300 newest (what a reader took from a feed a few seconds ago)
        long newest = Db.Scalar("SELECT max(id) FROM posts");
        var pass = new byte[PerPass][];
        long next = 0;
        while (_grown < Grown)
        {
            for (int i = 0; i < PerPass; i++)
            {
                if (i % 8 == 0)
                {
                    pass[i] = NewPost();
                    newest++;
                    _grown++;
                }
                else pass[i] = NewLike(newest - Rng.Next(300));
            }
            next = 0;
            Pass(pass, ref next);
        }
        newest = Db.Scalar("SELECT max(id) FROM posts");

        _feed = [Bytes($"GET /feed{Head}\r\n")];
        _missing = [Bytes($"GET /nothing{Head}\r\n")];
        _post = new byte[Variants][];
        _like = new byte[Variants][];
        _create = new byte[Variants][];
        _mix = new byte[Variants][];
        for (int i = 0; i < Variants; i++)
        {
            // a reader opens a post of the feed it got a few seconds ago
            _post[i] = Bytes($"GET /posts/{newest - Rng.Next(300)}{Head}\r\n");
            _like[i] = NewLike(newest - Rng.Next(300));
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

/// <summary>
/// Without BENCH_JOBS the benchmarks run in this process. BENCH_JOBS is a list of runtimes to compare, each in its
/// own process, separated by spaces:
/// <list type="bullet">
/// <item>jit10, jit11: the JIT with its defaults (tiering, tiered PGO). A suffix sets its switches: -nopgo
/// (DOTNET_TieredPGO=0), -tc0 (DOTNET_TieredCompilation=0), -noquick (DOTNET_TC_QuickJit=0), -nor2r
/// (DOTNET_ReadyToRun=0).</item>
/// <item>aot10, aot11: NativeAOT as the entry builds it (speed, x86-64-v3 on x64). Suffixes: -v2, -v4 and -native for
/// the instruction set, -blended for the default optimization preference, -size.</item>
/// </list>
/// The project must target both frameworks (Frameworks="net10.0;net11.0" in the environment) and the SDK must be .NET 11's to run
/// the 11 jobs. On Linux, with AOT_SQLITE set to the entry's native/libsqlite3.a, the NativeAOT jobs link SQLite and
/// libc as the entry does (aot.targets, which the environment hands to every build BenchmarkDotNet starts).
/// </summary>
static class Program
{
    static Job Make(string name)
    {
        string[] parts = name.Split('-');
        string tfm = parts[0].EndsWith("11") ? "net11.0" : "net10.0";
        // one launch, enough iterations for a ±1 % interval on a quiet machine
        Job job = Job.Default.WithLaunchCount(1).WithWarmupCount(6).WithIterationCount(15).WithId(name);
        if (parts[0].StartsWith("jit"))
        {
            job = job.WithRuntime(CoreRuntime.CreateForNewVersion(tfm, ".NET " + tfm[3..]))
                .WithToolchain(CsProjCoreToolchain.From(new NetCoreAppSettings(tfm, null, ".NET " + tfm[3..])));
            var env = new List<EnvironmentVariable>();
            foreach (string flag in parts.Skip(1))
                env.Add(flag switch
                {
                    "nopgo" => new EnvironmentVariable("DOTNET_TieredPGO", "0"),
                    "tc0" => new EnvironmentVariable("DOTNET_TieredCompilation", "0"),
                    "noquick" => new EnvironmentVariable("DOTNET_TC_QuickJit", "0"),
                    "nor2r" => new EnvironmentVariable("DOTNET_ReadyToRun", "0"),
                    _ => throw new ArgumentException($"unknown switch {flag} in {name}"),
                });
            return env.Count > 0 ? job.WithEnvironmentVariables(env.ToArray()) : job;
        }
        if (!parts[0].StartsWith("aot")) throw new ArgumentException($"unknown job {name}");
        bool x64 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64;
        string instructions = x64 ? "x86-64-v3" : "", preference = "Speed";
        foreach (string flag in parts.Skip(1))
            switch (flag)
            {
                case "v2": instructions = x64 ? "x86-64-v2" : ""; break;
                case "v4": instructions = x64 ? "x86-64-v4" : ""; break;
                case "native": instructions = "native"; break;
                case "blended": preference = ""; break;
                case "size": preference = "Size"; break;
                default: throw new ArgumentException($"unknown switch {flag} in {name}");
            }
        NativeAotToolchainBuilder builder = NativeAotToolchain.CreateBuilder().UseNuGet()
            .IlcInstructionSet(instructions).IlcOptimizationPreference(preference);
        builder.TargetFrameworkMoniker(tfm).DisplayName(name);
        return job.WithRuntime(NativeAotRuntime.Net10_0).WithToolchain(builder.ToToolchain());
    }

    static void Main(string[] args)
    {
        IConfig config = DefaultConfig.Instance.WithOptions(ConfigOptions.DisableOptimizationsValidator);
        if (Environment.GetEnvironmentVariable("BENCH_JOBS") is { Length: > 0 } jobs)
        {
            Environment.SetEnvironmentVariable("CustomAfterMicrosoftCommonTargets", System.IO.Path.Combine(AppContext.BaseDirectory, "aot.targets"));
            foreach (string name in jobs.Split(' ', StringSplitOptions.RemoveEmptyEntries)) config = config.AddJob(Make(name));
        }
        else config = config.AddJob(Job.Default.WithToolchain(BenchmarkDotNet.Toolchains.InProcess.Emit.InProcessEmitToolchain.Instance));
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}
