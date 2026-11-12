using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

/// <summary>Builds a database file with the challenge's schema and opens the entry's database layer on it.</summary>
static unsafe class Harness
{
    [DllImport("sqlite3")]
    static extern int sqlite3_close(nint db);

    public static readonly string Schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema.sql"));

    static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>A new database file: <paramref name="schema"/>, then <paramref name="sql"/>.</summary>
    public static string Create(string sql, string? schema = null)
    {
        Sqlite.initialize();
        string dir = Directory.CreateTempSubdirectory("twelve-").FullName;
        string path = Path.Combine(dir, "feed.db");
        nint db;
        fixed (byte* p = Z(path))
            Assert.Equal(Sqlite.Ok, Sqlite.open_v2(p, &db, 0x06, null));
        Run(db, (schema ?? Schema) + "; PRAGMA journal_mode = WAL;");
        Run(db, sql);
        Assert.Equal(Sqlite.Ok, sqlite3_close(db));
        return path;
    }

    public static void Run(nint db, string sql)
    {
        fixed (byte* p = Z(sql))
        {
            int rc = Sqlite.exec(db, p, 0, 0, 0);
            Assert.True(rc == Sqlite.Ok, $"{Marshal.PtrToStringUTF8((nint)Sqlite.errmsg(db))}: {sql[..Math.Min(sql.Length, 200)]}");
        }
    }

    /// <summary>Opens the entry on a database and turns its B-tree reads on as the server does at startup.</summary>
    public static void Open(string path)
    {
        Reads.On = false;
        Db.Open(path, 8192);
        Db.StartReads();
    }

    public static string Users(int n)
    {
        var sb = new StringBuilder("BEGIN;");
        for (int i = 1; i <= n; i++) sb.Append($"INSERT INTO users (id, username, created_at) VALUES ({i}, 'user_{i}', '2026-01-01T00:00:00.000Z');");
        return sb.Append("COMMIT;").ToString();
    }

    public static string Text() => Encoding.UTF8.GetString(Json.Body.Written);

    /// <summary>The feed from the B-tree path (null when it leaves the answer to SQL) and from the reference query.</summary>
    public static (string? Btree, string Sql) Feeds()
    {
        Db.Begin();
        Json.Clear();
        string? bt = Reads.Feed() ? Text() : null;
        if (bt is null) Assert.Equal(0, Json.Body.Length); // nothing half-written is left behind
        Json.Clear();
        Api.FeedSql();
        string sql = Text();
        Json.Clear();
        return (bt, sql);
    }

    /// <summary>A post from the B-tree path (its result code and what it wrote) and from the reference query (null
    /// when there is no such post).</summary>
    public static (int Code, string Btree, string? Sql) Posts(long id)
    {
        Db.Begin();
        Json.Clear();
        int code = Reads.GetPost(id);
        string bt = Text();
        Json.Clear();
        string? sql = Api.GetPostSql(id) ? Text() : null;
        Json.Clear();
        return (code, bt, sql);
    }

    /// <summary>Checks that every read the B-tree path answers is what SQL answers, and that it answers all of
    /// them unless <paramref name="covered"/> is false.</summary>
    public static void AssertSameAsSql(IEnumerable<long> ids, bool covered = true)
    {
        var (bt, sql) = Feeds();
        if (covered) Assert.Equal(sql, bt);
        else if (bt is not null) Assert.Equal(sql, bt);
        foreach (long id in ids)
        {
            var (code, post, expected) = Posts(id);
            if (code == Reads.UseSql)
            {
                Assert.False(covered, $"post {id} was left to SQL");
                Assert.Equal("", post);
                continue;
            }
            Assert.Equal(expected is null ? Reads.Missing : Reads.Found, code);
            Assert.Equal(expected ?? "", post);
            Assert.Equal(code, Reads.Exists(id));
        }
        Assert.True(Db.Commit());
    }

    /// <summary>One request through the HTTP layer, in a pass of its own: the status and the body.</summary>
    public static (int Status, string Body) Request(string head, string body = "")
    {
        var o = new Arena(1 << 16);
        Api.Tick();
        int flags = 0;
        byte[] request = Encoding.UTF8.GetBytes(head + "\r\n" + (body.Length > 0 ? $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" : "") + "\r\n" + body);
        Assert.Equal(request.Length, Http.Serve(request, o, ref flags));
        if (Db.InTxn) Assert.True(Db.Commit());
        string response = Encoding.UTF8.GetString(o.Written);
        int split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return (int.Parse(response.AsSpan(9, 3)), response[(split + 4)..]);
    }
}
