using System;
using System.Linq;
using System.Text;
using Xunit;

/// <summary>The load generator reads from a response what a browser or k6 would: a load test whose users read the
/// wrong posts measures another workload than the challenge's.</summary>
public class LoadgenTests
{
    static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void The_entry_s_feed_gives_a_user_the_feed_s_20_post_ids()
    {
        var sql = new StringBuilder(Harness.Users(3));
        for (int id = 1; id <= 25; id++)
            sql.Append($"INSERT INTO posts (id, user_id, body, created_at) VALUES ({id}, 1, 'p', '2026-01-01T00:00:{id:00}.000Z');");
        Harness.Open(Harness.Create(sql.ToString()));
        var o = new Arena(1 << 16);
        Api.Tick();
        int flags = 0;
        byte[] request = Bytes("GET /feed HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.Equal(request.Length, Http.Serve(request, o, ref flags));
        Assert.True(Db.Commit());
        ReadOnlySpan<byte> response = o.Written;
        int headEnd = response.IndexOf("\r\n\r\n"u8);

        Assert.True(ResponseHead.TryParse(response[..headEnd], out int status, out int length));
        Assert.Equal(200, status);
        Assert.Equal(response.Length - headEnd - 4, length);
        var ids = new long[20];
        Assert.Equal(20, ResponseHead.PostIds(response.Slice(headEnd + 4, length), ids));
        Assert.Equal(Enumerable.Range(6, 20).Reverse().Select(i => (long)i), ids);
    }

    [Theory]
    [InlineData("Content-Length: 4321", 4321)]
    [InlineData("Content-Length:4321", 4321)]
    [InlineData("Content-Length:    4321", 4321)]        // the entry's head: digits right-aligned in 7 columns
    [InlineData("Content-Length:\t4321", 4321)]
    [InlineData("Content-Length: 4321  \t", 4321)]
    [InlineData("content-length: 7", 7)]
    [InlineData("CONTENT-LENGTH: 7", 7)]
    [InlineData("Content-Length: 0", 0)]
    [InlineData("Content-Length: 0004321", 4321)]
    [InlineData("Content-Length: 2147483647", int.MaxValue)]
    [InlineData("X-Content-Length: 9\r\nContent-Length: 12", 12)]
    [InlineData("X-Note: Content-Length: 9\r\nContent-Length: 12", 12)]
    [InlineData("Server: x", 0)]                          // no body
    public void A_head_s_body_length_is_read_as_http_writes_it(string header, int expected)
    {
        Assert.True(ResponseHead.TryParse(Bytes($"HTTP/1.1 200 OK\r\nDate: now\r\n{header}\r\nConnection: keep-alive"), out int status, out int length));
        Assert.Equal((200, expected), (status, length));
        // and as the last or the only header
        Assert.True(ResponseHead.TryParse(Bytes($"HTTP/1.1 404 Not Found\r\n{header}"), out status, out length));
        Assert.Equal((404, expected), (status, length));
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: abc")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: ")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: -5")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 12 34")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 12x")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 2147483648")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 99999999999999999999")]
    [InlineData("HTTP/1.1 2x0 OK\r\nContent-Length: 1")]
    [InlineData("HTTP/1.1 20")]
    [InlineData("\"like_count\":3}]}")]                   // the rest of a body read as a head
    [InlineData("")]
    public void A_head_that_is_not_a_response_is_refused(string head)
    {
        Assert.False(ResponseHead.TryParse(Bytes(head), out _, out _));
    }

    [Theory]
    [InlineData("{\"posts\":[]}", new long[0])]
    [InlineData("", new long[0])]
    [InlineData("{\"posts\":[{\"id\":7,\"body\":\"a\"}]}", new long[] { 7 })]
    [InlineData("{\"posts\":[{\"id\":500123,\"body\":\"{\\\"id\\\":x\"},{\"id\":9007199254740993,\"body\":\"b\"}]}", new long[] { 500123, 9007199254740993 })]
    public void Post_ids_of_a_feed(string body, long[] expected)
    {
        var ids = new long[20];
        int n = ResponseHead.PostIds(Bytes(body), ids);
        Assert.Equal(expected, ids[..n]);
    }

    [Fact]
    public void Post_ids_stop_at_the_room_given()
    {
        string body = "{\"posts\":[" + string.Join(",", Enumerable.Range(1, 30).Select(i => $"{{\"id\":{i}}}")) + "]}";
        var ids = new long[20];
        Assert.Equal(20, ResponseHead.PostIds(Bytes(body), ids));
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), ids);
    }
}
