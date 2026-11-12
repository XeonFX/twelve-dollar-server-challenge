using System;
using System.Net;
using System.Net.Sockets;

static class Program
{
    static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    static IPAddress Address(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress? ip)) return ip;
        foreach (IPAddress a in Dns.GetHostAddresses(host))
            if (a.AddressFamily == AddressFamily.InterNetwork) return a;
        throw new ArgumentException($"HOST {host} has no IPv4 address");
    }

    static int Main()
    {
        try
        {
            Auth.Init(Env("JWT_SECRET", "twelve-dollar-challenge"));
            string path = Env("SQLITE_PATH", "feed.db");
            // SQLite's page cache: the whole seed (250 MB) fits, so a read never waits for the disk
            Db.Open(path, int.Parse(Env("CACHE_MB", "256")) * 1024);
            Db.Warm();
            Db.StartReads();
            Db.StartCheckpoints();
            string host = Env("HOST", "127.0.0.1");
            int port = int.Parse(Env("PORT", "3000"));
            int fd = Native.Listen(Address(host), port);
            var loop = new Loop(fd);
            Console.WriteLine($"listening on {host}:{port}, database {path}, reads: {(Reads.On ? "B-tree" : "SQL")}");
            loop.Run();
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"server: {e.Message}");
            return 1;
        }
    }
}
