using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

/// <summary>The B-tree reads against the reference SQL queries: the rows they must cover, the rows they must leave
/// to SQL, and the same reads after writes in the same pass.</summary>
public class ReadsTests
{
    const string T = "2026-01-01T00:00:00.000Z";

    static string Post(long id, long user, string body, string created = T) =>
        $"INSERT INTO posts (id, user_id, body, created_at) VALUES ({id}, {user}, '{body.Replace("'", "''")}', '{created}');";

    static string Like(long user, long post) => $"INSERT INTO likes (user_id, post_id, created_at) VALUES ({user}, {post}, '{T}');";

    static string Stamp(int second) => $"2026-01-01T00:{second / 60:00}:{second % 60:00}.000Z";

    static string Token(long sub, string username)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        string payload = B64(Encoding.UTF8.GetBytes($"{{\"sub\":\"{sub}\",\"username\":\"{username}\",\"exp\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600}}}"));
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes("secret"));
        return $"{head}.{payload}.{B64(h.ComputeHash(Encoding.UTF8.GetBytes($"{head}.{payload}")))}";
    }

    [Fact]
    public void Empty_database_gives_an_empty_feed_and_no_post()
    {
        Harness.Open(Harness.Create(""));
        Assert.True(Reads.On);
        var (bt, sql) = Harness.Feeds();
        Assert.Equal("{\"posts\":[]}", bt);
        Assert.Equal(bt, sql);
        Assert.Equal(Reads.Missing, Harness.Posts(1).Code);
        Assert.Equal(Reads.Missing, Reads.Exists(1));
        Assert.True(Db.Commit());
    }

    [Fact]
    public void One_post_is_written_exactly()
    {
        Harness.Open(Harness.Create(Harness.Users(3) + Post(7, 2, "hello") + Like(1, 7) + Like(3, 7)));
        Assert.True(Reads.On);
        const string post = "{\"id\":7,\"body\":\"hello\",\"created_at\":\"2026-01-01T00:00:00.000Z\",\"author\":\"user_2\",\"like_count\":2}";
        Assert.Equal("{\"posts\":[" + post + "]}", Harness.Feeds().Btree);
        var (code, bt, sql) = Harness.Posts(7);
        Assert.Equal(Reads.Found, code);
        Assert.Equal("{\"post\":" + post + "}", bt);
        Assert.Equal(bt, sql);
        Assert.Equal((Reads.Missing, ""), (Harness.Posts(6).Code, Harness.Posts(6).Btree));
        Assert.Equal(Reads.Missing, Harness.Posts(8).Code);
        Assert.Equal(Reads.Found, Reads.Exists(7));
        Assert.Equal(Reads.Missing, Reads.Exists(8));
        Assert.True(Db.Commit());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(45)]
    public void Feed_is_the_20_newest_by_time_then_id(int posts)
    {
        // times repeat (three posts share each second), so the id breaks ties
        var sql = new StringBuilder(Harness.Users(5));
        for (int id = 1; id <= posts; id++) sql.Append(Post(id, id % 5 + 1, $"post {id}", Stamp(id / 3)));
        Harness.Open(Harness.Create(sql.ToString()));
        string feed = Harness.Feeds().Btree!;
        var ids = System.Text.RegularExpressions.Regex.Matches(feed, "\"id\":(\\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        Assert.Equal(Enumerable.Range(1, posts).Reverse().Take(20), ids);
        Harness.AssertSameAsSql(Enumerable.Range(0, posts + 2).Select(i => (long)i));
    }

    [Fact]
    public void Feed_order_follows_created_at_not_id()
    {
        // the oldest id is the newest post
        Harness.Open(Harness.Create(Harness.Users(2) + Post(1, 1, "newest", Stamp(30)) + Post(2, 2, "oldest", Stamp(10)) + Post(3, 1, "middle", Stamp(20))));
        string feed = Harness.Feeds().Btree!;
        Assert.True(feed.IndexOf("newest", StringComparison.Ordinal) < feed.IndexOf("middle", StringComparison.Ordinal));
        Assert.True(feed.IndexOf("middle", StringComparison.Ordinal) < feed.IndexOf("oldest", StringComparison.Ordinal));
        Harness.AssertSameAsSql([1, 2, 3]);
    }

    [Fact]
    public void Like_counts_are_exact_for_ids_close_together_and_far_apart()
    {
        // the feed's ids span 511 with a gap of 510 (one index walk covers them) and 512 with 511 (a seek per post)
        foreach (long gap in new long[] { 3, 510, 511, 512, 100_000 })
        {
            var sql = new StringBuilder(Harness.Users(40));
            long far = 100 + gap;
            long[] ids = [100, far, 101, 99];
            for (int i = 0; i < ids.Length; i++) sql.Append(Post(ids[i], 1, $"p{i}", Stamp(i)));
            // likes: 0 on the first, 3 on the second, 40 on the third, 1 on the fourth, and some on ids with no post
            for (int u = 1; u <= 3; u++) sql.Append(Like(u, far));
            for (int u = 1; u <= 40; u++) sql.Append(Like(u, 101));
            sql.Append(Like(5, 99)).Append(Like(1, 98)).Append(Like(1, 102)).Append(Like(2, 102)).Append(Like(9, far + 1)).Append(Like(9, far - 1));
            Harness.Open(Harness.Create(sql.ToString()));
            string feed = Harness.Feeds().Btree!;
            Assert.Contains("\"id\":100,\"body\":\"p0\",\"created_at\":\"2026-01-01T00:00:00.000Z\",\"author\":\"user_1\",\"like_count\":0}", feed);
            Assert.Contains($"\"id\":{far},\"body\":\"p1\",\"created_at\":\"2026-01-01T00:00:01.000Z\",\"author\":\"user_1\",\"like_count\":3}}", feed);
            Assert.Contains("\"id\":101,\"body\":\"p2\",\"created_at\":\"2026-01-01T00:00:02.000Z\",\"author\":\"user_1\",\"like_count\":40}", feed);
            Assert.Contains("\"id\":99,\"body\":\"p3\",\"created_at\":\"2026-01-01T00:00:03.000Z\",\"author\":\"user_1\",\"like_count\":1}", feed);
            Harness.AssertSameAsSql([98, 99, 100, 101, 102, far - 1, far, far + 1]);
        }
    }

    [Fact]
    public void A_post_with_thousands_of_likes_counts_across_index_pages()
    {
        var sql = new StringBuilder(Harness.Users(6000));
        sql.Append(Post(1, 1, "a")).Append(Post(2, 1, "b")).Append(Post(3, 1, "c")).Append("BEGIN;");
        for (int u = 1; u <= 6000; u++) sql.Append(Like(u, 2));
        sql.Append(Like(7, 1)).Append(Like(8, 3)).Append(Like(9, 3)).Append("COMMIT;");
        Harness.Open(Harness.Create(sql.ToString()));
        Assert.Contains("\"like_count\":6000}", Harness.Posts(2).Btree);
        Assert.Contains("\"like_count\":1}", Harness.Posts(1).Btree);
        Assert.Contains("\"like_count\":2}", Harness.Posts(3).Btree);
        Harness.AssertSameAsSql([0, 1, 2, 3, 4]);
    }

    [Fact]
    public void Ids_at_the_edges_of_the_integer_range()
    {
        // 0 and 1 are stored as the constants 0 and 1, the others in 1, 2, 3, 4, 6 and 8 bytes, negative ones too
        long[] ids = [0, 1, -1, 127, 128, -129, 32767, 32768, 8388607, 8388608, 2147483647, 2147483648, 140737488355327,
            140737488355328, long.MaxValue, long.MinValue + 1];
        var sql = new StringBuilder(Harness.Users(3));
        for (int i = 0; i < ids.Length; i++) sql.Append(Post(ids[i], i % 3 + 1, $"p{i}", Stamp(i))).Append(Like(1, ids[i])).Append(Like(2, ids[i]));
        Harness.Open(Harness.Create(sql.ToString()));
        Assert.True(Reads.On);
        foreach (long id in ids)
        {
            var (code, bt, _) = Harness.Posts(id);
            Assert.Equal(Reads.Found, code);
            Assert.StartsWith($"{{\"post\":{{\"id\":{id},", bt);
            Assert.EndsWith("\"like_count\":2}}", bt);
        }
        Harness.AssertSameAsSql(ids.Concat([2, -2, long.MaxValue - 1, long.MinValue]));
    }

    [Fact]
    public void Bodies_with_escapes_unicode_and_the_longest_allowed()
    {
        string longest = string.Concat(Enumerable.Repeat("\U0001F600", 500)); // 500 characters, 2,000 bytes
        string[] bodies = ["quote \" backslash \\ slash /", "tab\tnewline\ncr\r bell\u0007 nul-free", "żółć 日本語 \u2028", longest, "x", new string('y', 500)];
        var sql = new StringBuilder(Harness.Users(2));
        for (int i = 0; i < bodies.Length; i++) sql.Append(Post(i + 1, 1, bodies[i], Stamp(i)));
        Harness.Open(Harness.Create(sql.ToString()));
        Assert.True(Reads.On);
        Assert.Contains("\"body\":\"quote \\\" backslash \\\\ slash /\"", Harness.Posts(1).Btree);
        Assert.Contains("\"body\":\"tab\\tnewline\\ncr\\r bell\\u0007 nul-free\"", Harness.Posts(2).Btree);
        Assert.Contains("\"body\":\"" + longest + "\"", Harness.Posts(4).Btree);
        Harness.AssertSameAsSql([1, 2, 3, 4, 5, 6]);
    }

    public static IEnumerable<object[]> Uncovered => new[]
    {
        // a username past a page: the users row has an overflow page
        new object[] { "overflowing user row", $"UPDATE users SET username = '{new string('n', 6000)}' WHERE id = 1;" },
        new object[] { "post without its user", "DELETE FROM users WHERE id = 1;" },
        new object[] { "body stored as a blob", "UPDATE posts SET body = X'68690A' WHERE id = 2;" },
        new object[] { "created_at stored as a blob", "UPDATE posts SET created_at = X'3230' WHERE id = 2;" },
        new object[] { "user_id that is not an integer", "UPDATE posts SET user_id = 'abc' WHERE id = 2;" },
        new object[] { "username stored as a blob", "UPDATE users SET username = X'6162' WHERE id = 1;" },
    };

    [Theory]
    [MemberData(nameof(Uncovered))]
    public void Rows_the_decoder_does_not_cover_are_answered_by_sql(string what, string damage)
    {
        _ = what;
        string path = Harness.Create(Harness.Users(2) + Post(1, 2, "fine") + Post(2, 1, "damaged", Stamp(5)) + Post(3, 2, "also fine", Stamp(9)) + Like(2, 2) + damage);
        Harness.Open(path);
        // the feed holds the damaged row, so the whole feed is SQL's; post 2 is too
        var (bt, sql) = Harness.Feeds();
        Assert.Null(bt);
        var (code, written, expected) = Harness.Posts(2);
        Assert.Equal(Reads.UseSql, code);
        Assert.Equal("", written);
        Assert.True(Db.Commit());
        // and through the HTTP layer the answers are the reference queries'
        var feed = Harness.Request("GET /feed HTTP/1.1");
        Assert.Equal((200, sql), feed);
        var post = Harness.Request("GET /posts/2 HTTP/1.1");
        Assert.Equal(expected is null ? (404, "{\"error\":\"post not found\"}") : (200, expected), post);
    }

    [Fact]
    public void A_different_column_order_turns_the_btree_reads_off()
    {
        string schema = Harness.Schema.Replace(
            "  user_id     INTEGER NOT NULL REFERENCES users(id),\n  body        TEXT NOT NULL CHECK (length(body) BETWEEN 1 AND 500),",
            "  body        TEXT NOT NULL CHECK (length(body) BETWEEN 1 AND 500),\n  user_id     INTEGER NOT NULL REFERENCES users(id),");
        Assert.NotEqual(Harness.Schema, schema);
        Harness.Open(Harness.Create(Harness.Users(1) + Post(1, 1, "swapped"), schema));
        Assert.False(Reads.On);
        Assert.Equal((200, "{\"post\":{\"id\":1,\"body\":\"swapped\",\"created_at\":\"2026-01-01T00:00:00.000Z\",\"author\":\"user_1\",\"like_count\":0}}"),
            Harness.Request("GET /posts/1 HTTP/1.1"));
    }

    [Fact]
    public void An_ascending_feed_index_turns_the_btree_reads_off()
    {
        string schema = Harness.Schema.Replace("(created_at DESC, id DESC)", "(created_at, id)");
        Assert.NotEqual(Harness.Schema, schema);
        Harness.Open(Harness.Create(Harness.Users(1) + Post(1, 1, "old", Stamp(1)) + Post(2, 1, "new", Stamp(2)), schema));
        Assert.False(Reads.On);
        string feed = Harness.Request("GET /feed HTTP/1.1").Body;
        Assert.True(feed.IndexOf("new", StringComparison.Ordinal) < feed.IndexOf("old", StringComparison.Ordinal));
    }

    [Fact]
    public void A_schema_change_after_startup_sends_reads_to_sql()
    {
        Harness.Open(Harness.Create(Harness.Users(1) + Post(1, 1, "kept")));
        Assert.True(Reads.On);
        Harness.Run(Db.Connection, "CREATE TABLE extra (x)");
        Db.Begin();
        Assert.False(Reads.Feed());
        Assert.Equal(Reads.UseSql, Reads.GetPost(1));
        Assert.Equal(Reads.UseSql, Reads.Exists(1));
        Assert.True(Db.Commit());
        Assert.Equal(200, Harness.Request("GET /posts/1 HTTP/1.1").Status);
    }

    [Fact]
    public void Reads_see_the_writes_of_their_own_pass_and_of_earlier_ones()
    {
        Auth.Init("secret");
        var sql = new StringBuilder(Harness.Users(30));
        for (int id = 1; id <= 25; id++) sql.Append(Post(id, id, $"seed {id}", Stamp(id)));
        Harness.Open(Harness.Create(sql.ToString()));
        Assert.True(Reads.On);
        var o = new Arena(1 << 16);
        string auth = $"Authorization: Bearer {Token(7, "user_7")}\r\n";

        // one pass: feed (cursors now sit on the rows), a new post, a like, then the same reads again
        Api.Tick();
        Db.Begin();
        string before = Harness.Feeds().Btree!;
        int flags = 0;
        byte[] create = Encoding.UTF8.GetBytes($"POST /posts HTTP/1.1\r\n{auth}Content-Length: 16\r\n\r\n{{\"body\":\"fresh\"}}");
        Assert.Equal(create.Length, Http.Serve(create, o, ref flags));
        byte[] like = Encoding.UTF8.GetBytes($"POST /posts/26/like HTTP/1.1\r\n{auth}\r\n");
        Assert.Equal(like.Length, Http.Serve(like, o, ref flags));
        string responses = Encoding.UTF8.GetString(o.Written);
        Assert.Contains("HTTP/1.1 201 Created", responses);
        Assert.Contains("{\"post\":{\"id\":26,\"body\":\"fresh\",\"created_at\":\"", responses);
        Assert.Contains("{\"liked\":true,\"already_liked\":false,\"post_id\":26}", responses);
        var (after, afterSql) = Harness.Feeds();
        Assert.NotEqual(before, after);
        Assert.Equal(afterSql, after);
        Assert.StartsWith("{\"posts\":[{\"id\":26,\"body\":\"fresh\",", after);
        Assert.Contains("\"author\":\"user_7\",\"like_count\":1}", after);
        Assert.DoesNotContain("\"id\":6,", after); // the 21st newest left the feed
        var (code, post, postSql) = Harness.Posts(26);
        Assert.Equal((Reads.Found, postSql), (code, post));
        Assert.True(Db.Commit());

        // later passes
        Harness.AssertSameAsSql(Enumerable.Range(0, 30).Select(i => (long)i));
        Assert.Equal((200, "{\"liked\":true,\"already_liked\":true,\"post_id\":26}"), Harness.Request($"POST /posts/26/like HTTP/1.1\r\n{auth.TrimEnd()}"));
        Assert.Equal((404, "{\"error\":\"post not found\"}"), Harness.Request($"POST /posts/27/like HTTP/1.1\r\n{auth.TrimEnd()}"));
        Assert.Contains("\"like_count\":1}", Harness.Request("GET /posts/26 HTTP/1.1").Body);
    }

    [Fact]
    public void A_created_post_gets_the_time_in_the_schema_s_format()
    {
        Auth.Init("secret");
        Harness.Open(Harness.Create(Harness.Users(7)));
        DateTime start = DateTime.UtcNow.AddMilliseconds(-1);
        var (status, body) = Harness.Request($"POST /posts HTTP/1.1\r\nAuthorization: Bearer {Token(7, "user_7")}", "{\"body\":\"timed\"}");
        Assert.Equal(201, status);
        var m = System.Text.RegularExpressions.Regex.Match(body, "^\\{\"post\":\\{\"id\":1,\"body\":\"timed\",\"created_at\":\"(\\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\d\\.\\d{3}Z)\",\"author\":\"user_7\",\"like_count\":0\\}\\}$");
        Assert.True(m.Success, body);
        DateTime created = DateTime.Parse(m.Groups[1].Value, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.InRange(created, start, DateTime.UtcNow);
        // stored as returned, and what SQLite's own default would have written sorts the same way
        Assert.Equal(1, Db.Scalar($"SELECT count(*) FROM posts WHERE id = 1 AND created_at = '{m.Groups[1].Value}' AND length(created_at) = length(strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Random_databases_read_the_same_as_sql(int seed)
    {
        var rng = new Random(seed);
        int users = rng.Next(1, 60), posts = rng.Next(0, 400);
        var sql = new StringBuilder(Harness.Users(users)).Append("BEGIN;");
        var ids = new HashSet<long>();
        // ids dense, sparse or scattered over the whole range; times that collide
        int shape = seed % 3;
        while (ids.Count < posts)
            ids.Add(shape == 0 ? ids.Count + 1 : shape == 1 ? rng.Next(1, 40_000) : rng.NextInt64(long.MinValue + 1, long.MaxValue));
        foreach (long id in ids)
        {
            string body = new string((char)rng.Next('a', 'z'), rng.Next(1, 500)) + (rng.Next(4) == 0 ? "\"\\\n日" : "");
            sql.Append(Post(id, rng.Next(1, users + 1), body[..Math.Min(body.Length, 500)], Stamp(rng.Next(0, 50))));
        }
        var liked = new HashSet<(long, long)>();
        long[] all = ids.ToArray();
        for (int i = 0; i < posts * 3; i++)
        {
            long post = rng.Next(5) == 0 ? rng.Next(1, 100) : all[rng.Next(all.Length)];
            int user = rng.Next(1, users + 1);
            if (liked.Add((user, post))) sql.Append(Like(user, post));
        }
        sql.Append("COMMIT;");
        Harness.Open(Harness.Create(sql.ToString()));
        Assert.True(Reads.On);
        Harness.AssertSameAsSql(all.Concat(Enumerable.Range(0, 50).Select(i => (long)i)).Concat([long.MaxValue, long.MinValue]));
    }

    [Fact]
    public void The_seed_reads_the_same_as_sql()
    {
        string seed = Environment.GetEnvironmentVariable("SEED_DB") ?? "";
        if (seed.Length == 0 || !File.Exists(seed)) return; // set SEED_DB to seed/feed.db to run it
        string dir = Directory.CreateTempSubdirectory("twelve-seed-").FullName;
        string path = Path.Combine(dir, "feed.db");
        File.Copy(seed, path);
        try
        {
            Harness.Open(path);
            Assert.True(Reads.On);
            var rng = new Random(12);
            Harness.AssertSameAsSql(Enumerable.Range(0, 3000).Select(_ => (long)rng.Next(-5, 500_010)).Concat([1, 500_000, 500_001]));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
