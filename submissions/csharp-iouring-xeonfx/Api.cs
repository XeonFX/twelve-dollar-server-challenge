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

    /// <summary>Formats the Date header again when the second changed. The loop calls it once per pass.</summary>
    public static void Tick()
    {
        DateTime now = DateTime.UtcNow;
        long s = now.Ticks / TimeSpan.TicksPerSecond;
        if (s == _dateSecond) return;
        Utf8Formatter.TryFormat(now, Date, out _, 'R');
        _dateSecond = s;
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

    /// <summary>Sends the JSON body written so far, and clears it.</summary>
    static void RespondJson(Arena o, int status, int mode)
    {
        Respond(o, status, mode, Json.Body.Written);
        Json.Clear();
    }

    // ---- posts ----

    static void PostObject(long id, ReadOnlySpan<byte> body, ReadOnlySpan<byte> createdAt, ReadOnlySpan<byte> author, long likes)
    {
        Json.Raw("{\"id\":"u8);
        Json.Num(id);
        Json.Raw(",\"body\":"u8);
        Json.Str(body);
        Json.Raw(",\"created_at\":"u8);
        Json.Str(createdAt);
        Json.Raw(",\"author\":"u8);
        Json.Str(author);
        Json.Raw(",\"like_count\":"u8);
        Json.Num(likes);
        Json.Put((byte)'}');
    }

    /// <summary>A row of id, body, created_at, username, like count.</summary>
    static void PostRow(nint st) =>
        PostObject(Sqlite.column_int64(st, 0), Sqlite.Text(st, 1), Sqlite.Text(st, 2), Sqlite.Text(st, 3), Sqlite.column_int64(st, 4));

    // ---- endpoints ----

    public static void Health(Arena o, int mode)
    {
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
            Json.Raw("{\"status\":\"ok\",\"db\":\"ok\",\"uptime_s\":"u8);
            Json.Num((Environment.TickCount64 - StartedAt) / 1000);
            Json.Put((byte)'}');
            RespondJson(o, 200, mode);
        }
        catch (Exception e)
        {
            Json.Clear();
            Json.Raw("{\"status\":\"degraded\",\"db\":\"unreachable\",\"error\":"u8);
            Json.Str(Encoding.UTF8.GetBytes(e.Message));
            Json.Put((byte)'}');
            RespondJson(o, 503, mode);
        }
    }

    static readonly long[] FeedIds = new long[20];
    static readonly long[] FeedLikes = new long[20];

    public static void Feed(Arena o, int mode)
    {
        Db.Begin();
        nint ids = Db.FeedIds;
        int n = 0;
        try
        {
            while (n < 20 && Db.Row(ids)) FeedIds[n++] = Sqlite.column_int64(ids, 0);
        }
        finally
        {
            Sqlite.reset(ids);
        }
        bool run = n > 0;
        for (int i = 1; i < n && run; i++) run = FeedIds[i] == FeedIds[i - 1] - 1;
        if (!run || !FeedByRange(n))
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
        RespondJson(o, 200, mode);
    }

    /// <summary>The feed when its posts are one run of consecutive ids, newest first (the usual case: posts are
    /// created in id order): one rowid range scan reads the posts and one range scan of likes_post_id_idx counts all
    /// their likes, instead of a lookup of each. Same transaction as the id query, so one snapshot. False, with
    /// nothing written, when the rows do not match the ids (a post whose user is missing).</summary>
    static bool FeedByRange(int n)
    {
        long hi = FeedIds[0], lo = FeedIds[n - 1];
        Array.Clear(FeedLikes, 0, n);
        nint lr = Db.LikeRange;
        try
        {
            Sqlite.bind_int64(lr, 1, lo);
            Sqlite.bind_int64(lr, 2, hi);
            while (Db.Row(lr)) FeedLikes[hi - Sqlite.column_int64(lr, 0)] = Sqlite.column_int64(lr, 1);
        }
        finally
        {
            Sqlite.reset(lr);
        }
        nint st = Db.FeedRange;
        int k = 0;
        try
        {
            Sqlite.bind_int64(st, 1, lo);
            Sqlite.bind_int64(st, 2, hi);
            Json.Raw("{\"posts\":["u8);
            while (Db.Row(st))
            {
                if (k == n || Sqlite.column_int64(st, 0) != FeedIds[k])
                {
                    Json.Clear();
                    return false;
                }
                if (k > 0) Json.Put((byte)',');
                PostObject(FeedIds[k], Sqlite.Text(st, 1), Sqlite.Text(st, 2), Sqlite.Text(st, 3), FeedLikes[k]);
                k++;
            }
            Json.Raw("]}"u8);
        }
        finally
        {
            Sqlite.reset(st);
        }
        if (k == n) return true;
        Json.Clear();
        return false;
    }

    public static void GetPost(Arena o, int mode, long id)
    {
        Db.Begin();
        nint st = Db.Post;
        try
        {
            Sqlite.bind_int64(st, 1, id);
            if (!Db.Row(st))
            {
                Respond(o, 404, mode, PostNotFound);
                return;
            }
            Json.Raw("{\"post\":"u8);
            PostRow(st);
            Json.Put((byte)'}');
        }
        finally
        {
            Sqlite.reset(st);
        }
        RespondJson(o, 200, mode);
    }

    public static void Like(Arena o, int mode, long userId, long id)
    {
        Db.Begin();
        nint st = Db.InsertLike;
        bool inserted;
        try
        {
            Sqlite.bind_int64(st, 1, userId);
            Sqlite.bind_int64(st, 2, id);
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
        Json.Raw(inserted ? "{\"liked\":true,\"already_liked\":false,\"post_id\":"u8 : "{\"liked\":true,\"already_liked\":true,\"post_id\":"u8);
        Json.Num(id);
        Json.Put((byte)'}');
        RespondJson(o, inserted ? 201 : 200, mode);
    }

    static bool PostExists(long id)
    {
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
            fixed (byte* p = body)
                Sqlite.bind_text(st, 2, p, length, Sqlite.Transient);
            if (!Db.Row(st)) throw new SqliteException(Db.Error);
            Json.Raw("{\"post\":"u8);
            PostObject(Sqlite.column_int64(st, 0), body, Sqlite.Text(st, 1), username, 0);
            Json.Put((byte)'}');
            // stepping to DONE finishes the statement; the loop sends the response after the COMMIT
            if (Sqlite.step(st) != Sqlite.Done)
            {
                Json.Clear();
                throw new SqliteException(Db.Error);
            }
        }
        finally
        {
            Sqlite.reset(st);
        }
        RespondJson(o, 201, mode);
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
