using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;
using Xunit.Abstractions;

/// <summary>User-space cost of each request through the HTTP layer on a copy of the seed, in passes as the loop
/// runs them. Runs only with BENCH=1 and SEED_DB set; prints ns per request.</summary>
public class Bench(ITestOutputHelper output)
{
    [Fact]
    public void Requests()
    {
        string seed = Environment.GetEnvironmentVariable("SEED_DB") ?? "";
        if (Environment.GetEnvironmentVariable("BENCH") != "1" || !File.Exists(seed)) return;
        string dir = Directory.CreateTempSubdirectory("twelve-bench-").FullName;
        string path = Path.Combine(dir, "feed.db");
        File.Copy(seed, path);
        try
        {
            Reads.On = false;
            Db.Open(path, 262144);
            Db.Warm();
            Db.StartReads();
            Assert.True(Reads.On);
            Auth.Init("twelve-dollar-challenge");
            string token = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(seed)!, "tokens.json"))).RootElement[5].GetProperty("token").GetString()!;
            const string head = " HTTP/1.1\r\nHost: 10.0.0.5\r\nUser-Agent: Grafana k6/1.3.0\r\n";
            byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
            var rng = new Random(3);
            // requests made ahead, so the loop times only the server's work
            const int variants = 4096;
            var requests = new System.Collections.Generic.Dictionary<string, byte[][]>
            {
                ["404"] = [Bytes($"GET /nothing{head}\r\n")],
                ["feed"] = [Bytes($"GET /feed{head}\r\n")],
                ["post"] = new byte[variants][],
                ["like"] = new byte[variants][],
                ["create"] = new byte[variants][],
            };
            for (int i = 0; i < variants; i++)
            {
                requests["post"][i] = Bytes($"GET /posts/{500_000 - rng.Next(3000)}{head}\r\n");
                requests["like"][i] = Bytes($"POST /posts/{500_000 - rng.Next(3000)}/like{head}Content-Length: 0\r\nAuthorization: Bearer {token}\r\nContent-Type: application/json\r\n\r\n");
                requests["create"][i] = Bytes($"POST /posts{head}Content-Length: 75\r\nAuthorization: Bearer {token}\r\nContent-Type: application/json\r\n\r\n{{\"body\":\"user_00006 says hi at 2026-11-12T05:00:00.000Z (VU 6, iter {i:0000})\"}}");
            }
            var o = new Arena(1 << 22);
            foreach (string what in new[] { "404", "feed", "post", "like", "create", "mix" })
            {
                const int passes = 3000, perPass = 64;
                double best = double.MaxValue;
                for (int round = 0; round < 3; round++)
                {
                    var watch = Stopwatch.StartNew();
                    int n = 0, bad = 0;
                    for (int p = 0; p < passes; p++)
                    {
                        Api.Tick();
                        o.Length = 0;
                        for (int i = 0; i < perPass; i++, n++)
                        {
                            string kind = what != "mix" ? what : (i % 50) switch { < 23 => "feed", < 46 => "post", < 49 => "like", _ => "create" };
                            byte[][] set = requests[kind];
                            byte[] request = set[n % set.Length];
                            int flags = 0;
                            if (Http.Serve(request, o, ref flags) != request.Length) bad++;
                        }
                        if (Db.InTxn && !Db.Commit()) bad++;
                    }
                    Assert.Equal(0, bad);
                    best = Math.Min(best, watch.ElapsedTicks * 1e9 / Stopwatch.Frequency / n);
                }
                output.WriteLine($"{what,-7} {best,8:F0} ns/request");
            }
            // the parts of a feed, alone
            unsafe
            {
                byte[] body = Bytes("user_00006 says hi at 2026-11-12T05:00:00.000Z (VU 6, iter 12)"), created = Bytes("2026-11-12T05:00:00.000Z"), author = Bytes("user_00006");
                fixed (byte* b = body)
                fixed (byte* c = created)
                fixed (byte* a = author)
                {
                    var w = Stopwatch.StartNew();
                    for (int round = 0; round < 3; round++)
                    {
                    w.Restart();
                    for (int i = 0; i < 200_000; i++)
                    {
                        Json.Out = o;
                        o.Length = 0;
                        for (int k = 0; k < 20; k++) Json.Post(500_000 - k, b, body.Length, c, created.Length, a, author.Length, k % 3);
                    }
                    }
                    output.WriteLine($"20 post objects {w.ElapsedTicks * 1e9 / Stopwatch.Frequency / 200_000,8:F0} ns");
                }
                var w2 = Stopwatch.StartNew();
                for (int round = 0; round < 3; round++)
                {
                w2.Restart();
                for (int p = 0; p < 3000; p++)
                {
                    Db.Begin();
                    for (int i = 0; i < 64; i++)
                    {
                        Json.Out = o;
                        o.Length = 0;
                        Reads.Feed();
                    }
                    Db.Commit();
                }
                }
                output.WriteLine($"Reads.Feed      {w2.ElapsedTicks * 1e9 / Stopwatch.Frequency / (3000 * 64),8:F0} ns");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
