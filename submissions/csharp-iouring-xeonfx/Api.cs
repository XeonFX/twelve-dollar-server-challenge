using System;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;

/// <summary>The five endpoints. Each writes one whole HTTP/1.1 response (status line, headers, JSON body) into the
/// pass's arena. Every statement runs in the pass transaction (<see cref="Db.Begin"/>).</summary>
static class Api
{
    public static ReadOnlySpan<byte> NotFound => "{\"error\":\"not found\"}"u8;
    public static ReadOnlySpan<byte> InvalidId => "{\"error\":\"invalid post id\"}"u8;
    public static ReadOnlySpan<byte> PostNotFound => "{\"error\":\"post not found\"}"u8;
    public static ReadOnlySpan<byte> Malformed => "{\"error\":\"malformed JSON body\"}"u8;
    public static ReadOnlySpan<byte> BodyRequired => "{\"error\":\"body is required\"}"u8;
    public static ReadOnlySpan<byte> BodyTooLong => "{\"error\":\"body must be at most 500 characters\"}"u8;
    public static ReadOnlySpan<byte> Internal => "{\"error\":\"internal server error\"}"u8;

    /// <summary>How a response ends the connection: <see cref="KeepAlive"/> also says so (an HTTP/1.0 client that
    /// asked for it), <see cref="Close"/> says the server closes it.</summary>
    public const int Default = 0, Close = 1, KeepAlive = 2;

    static readonly long StartedAt = Environment.TickCount64;

    // ---- HTTP framing ----

    static readonly byte[] Date = new byte[29];
    static long _dateSecond = -1;
    /// <summary>The pass's time as the schema's created_at default writes it: UTC "YYYY-MM-DDTHH:MM:SS.mmmZ".
    /// Pinned: SQLite reads it while an insert runs.</summary>
    static readonly byte[] Stamp = GC.AllocateArray<byte>(24, pinned: true);

    /// <summary>Takes the time for the pass: the Date header (formatted again when the second changed) and the
    /// created_at of the rows the pass inserts. The loop calls it once per pass.</summary>
    public static void Tick()
    {
        DateTime now = DateTime.UtcNow;
        long s = now.Ticks / TimeSpan.TicksPerSecond;
        if (s != _dateSecond)
        {
            Utf8Formatter.TryFormat(now, Date, out _, 'R');
            // 'O' is YYYY-MM-DDTHH:MM:SS.fffffffZ for a UTC time: its first 20 bytes are the stamp's
            Span<byte> o = stackalloc byte[28];
            Utf8Formatter.TryFormat(now, o, out _, 'O');
            o[..20].CopyTo(Stamp);
            Stamp[23] = (byte)'Z';
            _dateSecond = s;
        }
        int ms = (int)(now.Ticks / TimeSpan.TicksPerMillisecond % 1000);
        Stamp[20] = (byte)('0' + ms / 100);
        Stamp[21] = (byte)('0' + ms / 10 % 10);
        Stamp[22] = (byte)('0' + ms % 10);
    }

    static ReadOnlySpan<byte> StatusLine(int status) => status switch
    {
        200 => "HTTP/1.1 200 OK\r\n"u8,
        201 => "HTTP/1.1 201 Created\r\n"u8,
        400 => "HTTP/1.1 400 Bad Request\r\n"u8,
        401 => "HTTP/1.1 401 Unauthorized\r\n"u8,
        404 => "HTTP/1.1 404 Not Found\r\n"u8,
        503 => "HTTP/1.1 503 Service Unavailable\r\n"u8,
        _ => "HTTP/1.1 500 Internal Server Error\r\n"u8,
    };

    /// <summary>Writes a whole response into <paramref name="o"/>.</summary>
    public static void Respond(Arena o, int status, int mode, ReadOnlySpan<byte> body)
    {
        ReadOnlySpan<byte> line = StatusLine(status);
        Span<byte> d = o.Reserve(line.Length + 128 + body.Length);
        line.CopyTo(d);
        int i = line.Length;
        "Content-Type: application/json\r\nDate: "u8.CopyTo(d[i..]);
        i += 38;
        Date.CopyTo(d[i..]);
        i += 29;
        "\r\nContent-Length: "u8.CopyTo(d[i..]);
        i += 18;
        Utf8Formatter.TryFormat(body.Length, d[i..], out int n);
        i += n;
        if (mode == Close)
        {
            "\r\nConnection: close"u8.CopyTo(d[i..]);
            i += 19;
        }
        else if (mode == KeepAlive)
        {
            "\r\nConnection: keep-alive"u8.CopyTo(d[i..]);
            i += 24;
        }
        "\r\n\r\n"u8.CopyTo(d[i..]);
        i += 4;
        body.CopyTo(d[i..]);
        o.Length += i + body.Length;
    }

    // "Content-Length: " and room for 7 digits, right-aligned: the head is written before the body
    const int LengthRoom = 7;

    /// <summary>Starts a response whose JSON body is written next, straight after its head (<see cref="Json"/>
    /// writes there from now on). Returns where the body starts, for <see cref="End"/>.</summary>
    static int Start(Arena o, int status, int mode)
    {
        Json.Out = o;
        ReadOnlySpan<byte> line = StatusLine(status);
        Span<byte> d = o.Reserve(line.Length + 160);
        line.CopyTo(d);
        int i = line.Length;
        "Content-Type: application/json\r\nDate: "u8.CopyTo(d[i..]);
        i += 38;
        Date.CopyTo(d[i..]);
        i += 29;
        if (mode == Close)
        {
            "\r\nConnection: close"u8.CopyTo(d[i..]);
            i += 19;
        }
        else if (mode == KeepAlive)
        {
            "\r\nConnection: keep-alive"u8.CopyTo(d[i..]);
            i += 24;
        }
        "\r\nContent-Length:        \r\n\r\n"u8.CopyTo(d[i..]);
        i += 18 + LengthRoom + 4;
        o.Length += i;
        return o.Length;
    }

    /// <summary>Ends the response whose body started at <paramref name="body"/>: fills in its length.</summary>
    static void End(Arena o, int body)
    {
        int length = o.Length - body;
        Span<byte> digits = o.Array.AsSpan(body - 4 - LengthRoom, LengthRoom);
        int at = LengthRoom;
        do
        {
            digits[--at] = (byte)('0' + length % 10);
            length /= 10;
        } while (length > 0 && at > 0);
        if (length > 0) throw new InvalidOperationException("response body too long");
    }

    // ---- posts ----

    public static unsafe void PostObject(long id, ReadOnlySpan<byte> body, ReadOnlySpan<byte> createdAt, ReadOnlySpan<byte> author, long likes)
    {
        fixed (byte* b = body)
        fixed (byte* c = createdAt)
        fixed (byte* a = author)
            Json.Post(id, b, body.Length, c, createdAt.Length, a, author.Length, likes);
    }

    /// <summary>A row of id, body, created_at, username, like count.</summary>
    static void PostRow(nint st) =>
        PostObject(Sqlite.column_int64(st, 0), Sqlite.Text(st, 1), Sqlite.Text(st, 2), Sqlite.Text(st, 3), Sqlite.column_int64(st, 4));

    // ---- endpoints ----

    public static void Health(Arena o, int mode)
    {
        int head = o.Length;
        try
        {
            Db.Begin();
            try
            {
                if (!Db.Row(Db.Ping)) throw new SqliteException(Db.Error);
            }
            finally
            {
                Sqlite.reset(Db.Ping);
            }
            int body = Start(o, 200, mode);
            Json.Raw("{\"status\":\"ok\",\"db\":\"ok\",\"uptime_s\":"u8);
            Json.Num((Environment.TickCount64 - StartedAt) / 1000);
            Json.Put((byte)'}');
            End(o, body);
        }
        catch (Exception e)
        {
            o.Length = head;
            int body = Start(o, 503, mode);
            Json.Raw("{\"status\":\"degraded\",\"db\":\"unreachable\",\"error\":"u8);
            Json.Str(Encoding.UTF8.GetBytes(e.Message));
            Json.Put((byte)'}');
            End(o, body);
        }
    }

    public static void Feed(Arena o, int mode)
    {
        Db.Begin();
        int body = Start(o, 200, mode);
        if (!Reads.On || !Reads.Feed()) FeedSql();
        End(o, body);
    }

    /// <summary>The feed as the reference query gives it.</summary>
    internal static void FeedSql()
    {
        nint st = Db.Feed;
        try
        {
            Json.Raw("{\"posts\":["u8);
            for (int k = 0; Db.Row(st); k++)
            {
                if (k > 0) Json.Put((byte)',');
                PostRow(st);
            }
            Json.Raw("]}"u8);
        }
        finally
        {
            Sqlite.reset(st);
        }
    }

    public static void GetPost(Arena o, int mode, long id)
    {
        Db.Begin();
        int head = o.Length;
        int body = Start(o, 200, mode);
        int found = Reads.On ? Reads.GetPost(id) : Reads.UseSql;
        if (found == Reads.UseSql) found = GetPostSql(id) ? Reads.Found : Reads.Missing;
        if (found == Reads.Found)
        {
            End(o, body);
            return;
        }
        o.Length = head;
        Respond(o, 404, mode, PostNotFound);
    }

    /// <summary>A post as the reference query gives it; false when there is none.</summary>
    internal static bool GetPostSql(long id)
    {
        nint st = Db.Post;
        try
        {
            Sqlite.bind_int64(st, 1, id);
            if (!Db.Row(st)) return false;
            Json.Raw("{\"post\":"u8);
            PostRow(st);
            Json.Put((byte)'}');
            return true;
        }
        finally
        {
            Sqlite.reset(st);
        }
    }

    public static unsafe void Like(Arena o, int mode, long userId, long id)
    {
        Db.Begin();
        nint st = Db.InsertLike;
        bool inserted;
        try
        {
            Sqlite.bind_int64(st, 1, userId);
            Sqlite.bind_int64(st, 2, id);
            fixed (byte* stamp = Stamp)
                Sqlite.bind_text(st, 3, stamp, 24, Sqlite.Static);
            if (Sqlite.step(st) != Sqlite.Done) throw new SqliteException(Db.Error);
            inserted = Db.Changes == 1;
        }
        finally
        {
            Sqlite.reset(st);
        }
        if (!inserted && !PostExists(id))
        {
            Respond(o, 404, mode, PostNotFound);
            return;
        }
        int body = Start(o, inserted ? 201 : 200, mode);
        Json.Raw(inserted ? "{\"liked\":true,\"already_liked\":false,\"post_id\":"u8 : "{\"liked\":true,\"already_liked\":true,\"post_id\":"u8);
        Json.Num(id);
        Json.Put((byte)'}');
        End(o, body);
    }

    static bool PostExists(long id)
    {
        int found = Reads.On ? Reads.Exists(id) : Reads.UseSql;
        if (found != Reads.UseSql) return found == Reads.Found;
        nint st = Db.PostExists;
        try
        {
            Sqlite.bind_int64(st, 1, id);
            return Db.Row(st);
        }
        finally
        {
            Sqlite.reset(st);
        }
    }

    public static unsafe void CreatePost(Arena o, int mode, long userId, ReadOnlySpan<byte> username, ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> error = ParseBody(data, out string? text);
        if (!error.IsEmpty)
        {
            Respond(o, 400, mode, error);
            return;
        }
        // at most 500 UTF-16 units, so at most 1,500 UTF-8 bytes
        Span<byte> body = stackalloc byte[1536];
        int length = Encoding.UTF8.GetBytes(text, body);
        body = body[..length];
        Db.Begin();
        nint st = Db.InsertPost;
        try
        {
            Sqlite.bind_int64(st, 1, userId);
            // both stay where they are until the reset below
            fixed (byte* p = body)
            fixed (byte* stamp = Stamp)
            {
                Sqlite.bind_text(st, 2, p, length, Sqlite.Static);
                Sqlite.bind_text(st, 3, stamp, 24, Sqlite.Static);
                if (Sqlite.step(st) != Sqlite.Done) throw new SqliteException(Db.Error);
            }
        }
        finally
        {
            Sqlite.reset(st);
        }
        // the loop sends the response after the COMMIT
        int start = Start(o, 201, mode);
        Json.Raw("{\"post\":"u8);
        PostObject(Db.LastId, body, Stamp, username, 0);
        Json.Put((byte)'}');
        End(o, start);
    }

    /// <summary>Checks, on the rows the database has at startup, that the B-tree reads write byte for byte what
    /// the reference queries write: the feed, its posts, posts spread over the whole table and ids that do not
    /// exist. Null when they do, else what differed.</summary>
    public static string? SelfTest()
    {
        var scratch = new Arena(1 << 16);
        Json.Out = scratch;
        byte[] Take()
        {
            byte[] written = scratch.Written.ToArray();
            scratch.Length = 0;
            return written;
        }
        Db.Begin();
        try
        {
            if (!Reads.Feed()) return "the feed is not covered";
            byte[] feed = Take();
            FeedSql();
            if (!Take().AsSpan().SequenceEqual(feed)) return "the feed differs";
            long max = Db.Scalar("SELECT max(id) FROM posts");
            var ids = new System.Collections.Generic.List<long>(Db.Column("SELECT id FROM posts ORDER BY created_at DESC, id DESC LIMIT 25"));
            ids.AddRange([0, 1, 2, max, max + 1, long.MaxValue, -1]);
            for (long k = 1; k <= 200; k++) ids.Add(max / 200 * k + k % 7);
            foreach (long id in ids)
            {
                int found = Reads.GetPost(id);
                byte[] post = Take();
                bool sql = GetPostSql(id);
                if (found == Reads.UseSql || (found == Reads.Found) != sql || !Take().AsSpan().SequenceEqual(post) || Reads.Exists(id) != found)
                    return $"post {id} differs";
            }
            return null;
        }
        finally
        {
            Db.Commit();
        }
    }

    /// <summary>JavaScript's String.prototype.trim() set, so bodies are trimmed as the reference servers do.</summary>
    static readonly char[] JsWhitespace =
        "\t\n\u000b\u000c\r \u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff".ToCharArray();

    /// <summary>The 400 body, or empty when the post body is valid. Reads the whole document, so trailing junk is an
    /// error; the last "body" key wins, as in JSON.parse.</summary>
    static ReadOnlySpan<byte> ParseBody(ReadOnlySpan<byte> data, out string? body)
    {
        body = null;
        string? value = null;
        bool isObject = false;
        try
        {
            var r = new Utf8JsonReader(data);
            while (r.Read())
            {
                if (r.CurrentDepth == 0 && r.TokenType == JsonTokenType.StartObject) isObject = true;
                else if (r.CurrentDepth == 1 && isObject && r.TokenType == JsonTokenType.PropertyName && r.ValueTextEquals("body"u8))
                {
                    r.Read();
                    value = r.TokenType == JsonTokenType.String ? r.GetString() : null;
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // GetString throws InvalidOperationException for a lone surrogate escape: not a valid string either
            return Malformed;
        }
        if (value is null) return BodyRequired;
        string trimmed = value.Trim(JsWhitespace);
        if (trimmed.Length == 0) return BodyRequired;
        if (trimmed.Length > 500) return BodyTooLong;
        body = trimmed;
        return default;
    }
}
