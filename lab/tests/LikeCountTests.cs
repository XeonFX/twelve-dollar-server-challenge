using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>like_count is <c>SELECT count(*) FROM likes WHERE post_id = ?</c> at the moment the request is served,
/// for the shapes a load test gives the likes: many on a few posts, on every post of the feed and on the ids around
/// them, arriving between the reads of one pass.</summary>
public class LikeCountTests
{
    const string T = "2026-01-01T00:00:00.000Z";

    static string Post(long id, int second = 0) =>
        $"INSERT INTO posts (id, user_id, body, created_at) VALUES ({id}, 1, 'p{id}', '2026-01-01T00:{second / 60:00}:{second % 60:00}.000Z');";

    static string Likes(long post, int from, int to)
    {
        var sb = new StringBuilder();
        for (int u = from; u <= to; u++) sb.Append($"INSERT INTO likes (user_id, post_id, created_at) VALUES ({u}, {post}, '{T}');");
        return sb.ToString();
    }

    static string Token(long sub)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        string payload = B64(Encoding.UTF8.GetBytes($"{{\"sub\":\"{sub}\",\"username\":\"user_{sub}\",\"exp\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600}}}"));
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes("secret"));
        return $"{head}.{payload}.{B64(h.ComputeHash(Encoding.UTF8.GetBytes($"{head}.{payload}")))}";
    }

    /// <summary>The (id, like_count) pairs of a feed or post body, in order.</summary>
    static (long Id, long Likes)[] Counts(string json) =>
        Regex.Matches(json, "\\{\"id\":(-?\\d+),\"body\":\"p-?\\d+\",\"created_at\":\"[^\"]+\",\"author\":\"user_1\",\"like_count\":(\\d+)\\}")
            .Select(m => (long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value))).ToArray();

    static long Sql(long post) => Db.Scalar($"SELECT count(*) FROM likes WHERE post_id = {post}");

    /// <summary>The B-tree feed and each of <paramref name="posts"/>, with every count checked against SQL's
    /// count for that id. Returns the feed's pairs.</summary>
    static (long Id, long Likes)[] AssertExact(params long[] posts)
    {
        var (feed, feedSql) = Harness.Feeds();
        Assert.NotNull(feed);
        Assert.Equal(feedSql, feed);
        var pairs = Counts(feed);
        Assert.Equal(Math.Min(20, Db.Scalar("SELECT count(*) FROM posts")), pairs.Length);
        foreach (var (id, likes) in pairs) Assert.Equal((id, Sql(id)), (id, likes));
        foreach (long id in posts)
        {
            var (code, post, _) = Harness.Posts(id);
            Assert.Equal(Reads.Found, code);
            Assert.Equal([(id, Sql(id))], Counts(post));
        }
        if (Db.InTxn) Assert.True(Db.Commit());
        return pairs;
    }

    [Theory]
    [InlineData(1)]      // only on a leaf's first cells
    [InlineData(400)]    // a few index pages
    [InlineData(9000)]   // an index three levels deep around it
    public void A_post_every_user_liked_between_posts_nobody_liked(int users)
    {
        var sql = new StringBuilder(Harness.Users(users)).Append("BEGIN;");
        for (int id = 1; id <= 5; id++) sql.Append(Post(id, id));
        sql.Append(Likes(3, 1, users)).Append("COMMIT;");
        Harness.Open(Harness.Create(sql.ToString()));
        var feed = AssertExact(1, 2, 3, 4, 5);
        Assert.Equal([(5L, 0L), (4, 0), (3, users), (2, 0), (1, 0)], feed);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(700, 700)]
    public void Likes_of_the_ids_just_outside_the_feed_s_are_not_counted(int below, int above)
    {
        // posts 100 to 119 are the feed; 99 is an older post, 120 has likes but no post (a post that is gone)
        var sql = new StringBuilder(Harness.Users(700)).Append("BEGIN;").Append(Post(99, 0));
        for (int id = 100; id <= 119; id++) sql.Append(Post(id, id));
        sql.Append(Likes(99, 1, below)).Append(Likes(120, 1, above)).Append(Likes(100, 1, 2)).Append(Likes(119, 1, 3)).Append("COMMIT;");
        Harness.Open(Harness.Create(sql.ToString()));
        var feed = AssertExact(99, 100, 119);
        Assert.Equal(20, feed.Length);
        Assert.Equal((119L, 3L), feed[0]);
        Assert.Equal((100L, 2L), feed[^1]);
        Assert.All(feed[1..^1], p => Assert.Equal(0, p.Likes));
        Assert.Equal(below, Sql(99));
        Assert.Equal(above, Sql(120));
    }

    [Theory]
    [InlineData(1)]         // one walk of the index
    [InlineData(511)]       // the widest span one walk takes
    [InlineData(512)]       // a seek per post
    [InlineData(1_000_000)]
    public void Many_likes_on_the_feed_s_posts_and_on_posts_between_their_ids(long step)
    {
        // the feed's ids are far apart (old posts with new times); the ids between them have likes of their own
        var sql = new StringBuilder(Harness.Users(300)).Append("BEGIN;");
        long[] ids = [1000, 1000 + step, 1000 + 2 * step];
        for (int i = 0; i < ids.Length; i++) sql.Append(Post(ids[i], 50 + i));
        sql.Append(Likes(ids[0], 1, 300)).Append(Likes(ids[2], 10, 150));
        // between: posts older than the feed's, and likes of ids that have no post
        for (long id = 1001; id < 1000 + Math.Min(step, 40); id++) sql.Append(Post(id, 1)).Append(Likes(id, 1, (int)(id % 7)));
        if (step > 2) sql.Append(Likes(ids[1] - 1, 1, 200)).Append(Likes(ids[1] + 1, 1, 200));
        // 20 newer posts would push these out of the feed: 17 keep all three in it
        for (int id = 1; id <= 17; id++) sql.Append(Post(id, 100 + id)).Append(Likes(id, 1, id));
        sql.Append("COMMIT;");
        Harness.Open(Harness.Create(sql.ToString()));
        var feed = AssertExact(ids);
        Assert.Equal(20, feed.Length);
        Assert.Contains((ids[0], 300L), feed);
        Assert.Contains((ids[1], 0L), feed);
        Assert.Contains((ids[2], 141L), feed);
        for (int id = 1; id <= 17; id++) Assert.Contains(((long)id, (long)id), feed);
    }

    [Fact]
    public void Posts_deleted_from_the_middle_of_the_feed_leave_their_likes_uncounted()
    {
        var sql = new StringBuilder(Harness.Users(50)).Append("BEGIN;");
        for (int id = 1; id <= 40; id++) sql.Append(Post(id, id)).Append(Likes(id, 1, id));
        sql.Append("COMMIT;");
        string path = Harness.Create(sql.ToString());
        Harness.Open(path);
        // the schema's references are not enforced on this connection: the deleted posts' likes stay in the index
        Db.Begin();
        Harness.Run(Db.Connection, "DELETE FROM posts WHERE id BETWEEN 30 AND 34 OR id = 40; DELETE FROM likes WHERE post_id = 36;");
        Assert.True(Db.Commit());
        Assert.Equal(32, Sql(32));
        var feed = AssertExact(1, 29, 35, 36, 39);
        long[] expected = [39, 38, 37, 36, 35, .. Enumerable.Range(15, 15).Reverse().Select(i => (long)i)];
        Assert.Equal(expected.Select(id => (id, id == 36 ? 0 : id)), feed);
        foreach (long gone in new long[] { 30, 34, 40 })
        {
            Db.Begin();
            Harness.Clear();
            Assert.Equal(Reads.Missing, Reads.GetPost(gone));
            Assert.Equal("", Harness.Text());
            Assert.True(Db.Commit());
        }
    }

    [Fact]
    public void Likes_that_arrive_between_the_reads_of_one_pass_are_counted_at_once()
    {
        // what a load test does to the newest posts: every user likes one of them, and reads go on in the same pass
        const int users = 2500;
        Auth.Init("secret");
        var sql = new StringBuilder(Harness.Users(users)).Append("BEGIN;");
        for (int id = 1; id <= 30; id++) sql.Append(Post(id, id));
        sql.Append("COMMIT;");
        Harness.Open(Harness.Create(sql.ToString()));
        var o = new Arena(1 << 20);
        var expected = new Dictionary<long, long>();
        var rng = new Random(5);
        byte[] feed = Encoding.UTF8.GetBytes("GET /feed HTTP/1.1\r\n\r\n");
        int user = 1;
        while (user <= users)
        {
            // a pass: likes and reads in turn, the cursors of the reads staying on the pages the likes change
            Api.Tick();
            o.Length = 0;
            var asked = new List<long>();
            for (int k = 0; k < 64 && user <= users; k++, user++)
            {
                // most on two posts, the rest over the feed and the older posts below it
                long post = rng.Next(10) < 6 ? 29 + rng.Next(2) : rng.Next(1, 31);
                expected[post] = expected.GetValueOrDefault(post) + 1;
                byte[] like = Encoding.UTF8.GetBytes($"POST /posts/{post}/like HTTP/1.1\r\nAuthorization: Bearer {Token(user)}\r\n\r\n");
                byte[] read = Encoding.UTF8.GetBytes($"GET /posts/{post} HTTP/1.1\r\n\r\n");
                int flags = 0;
                Assert.Equal(like.Length, Http.Serve(like, o, ref flags));
                Assert.Equal(read.Length, Http.Serve(read, o, ref flags));
                Assert.Equal(feed.Length, Http.Serve(feed, o, ref flags));
                asked.Add(post);
            }
            Assert.True(Db.Commit());
            // every response of the pass carried the count as it was when it was served
            string[] responses = Encoding.UTF8.GetString(o.Written).Split("HTTP/1.1 ", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(asked.Count * 3, responses.Length);
            var running = new Dictionary<long, long>(expected);
            for (int k = asked.Count - 1; k >= 0; k--)
            {
                long post = asked[k];
                Assert.StartsWith("201 Created", responses[k * 3]);
                Assert.Equal([(post, running[post])], Counts(responses[k * 3 + 1]));
                var pairs = Counts(responses[k * 3 + 2]);
                Assert.Equal(20, pairs.Length);
                foreach (var (id, likes) in pairs) Assert.Equal((id, running.GetValueOrDefault(id)), (id, likes));
                running[post]--;
            }
        }
        Assert.Equal(users, Db.Scalar("SELECT count(*) FROM likes"));
        foreach (var (post, likes) in expected) Assert.Equal(likes, Sql(post));
        AssertExact(1, 10, 11, 29, 30);
    }
}
