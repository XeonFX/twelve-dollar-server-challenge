using System;
using System.Text;

/// <summary>The HTTP/1.1 parser and the routes. It reads the request line and the Authorization, Content-Length,
/// Connection, Transfer-Encoding and Expect headers, and answers 100 Continue.</summary>
static class Http
{
    /// <summary>Connection flags. In: <see cref="ContinueSent"/> when the request at the start of the input already
    /// got its 100 Continue. Out: <see cref="Close"/> when the last response closes the connection,
    /// <see cref="Held"/> when a write waits for the WAL to catch up (the loop runs it again later).</summary>
    public const int Close = 1, ContinueSent = 2, Held = 4;

    const int MaxHead = 16384, MaxBody = 1 << 20;

    static ReadOnlySpan<byte> BadRequest => "{\"error\":\"bad request\"}"u8;

    /// <summary>Answers every complete request in <paramref name="s"/> into <paramref name="o"/>, in order, and
    /// returns the bytes consumed. Stops at a request that is not complete, at one that closes the connection and at
    /// a held write.</summary>
    public static int Serve(ReadOnlySpan<byte> s, Arena o, ref int flags)
    {
        int off = 0;
        while (true)
        {
            ReadOnlySpan<byte> rest = s[off..];
            int headEnd = rest.IndexOf("\r\n\r\n"u8);
            if (headEnd < 0)
            {
                if (rest.Length > MaxHead) Fail(o, ref flags);
                return off;
            }
            int lineEnd = rest.IndexOf("\r\n"u8);
            ReadOnlySpan<byte> line = rest[..lineEnd];
            int sp1 = line.IndexOf((byte)' ');
            int sp2 = sp1 < 0 ? -1 : line[(sp1 + 1)..].IndexOf((byte)' ');
            if (sp1 <= 0 || sp2 <= 0)
            {
                Fail(o, ref flags);
                return off;
            }
            sp2 += sp1 + 1;
            ReadOnlySpan<byte> method = line[..sp1];
            ReadOnlySpan<byte> path = line[(sp1 + 1)..sp2];
            ReadOnlySpan<byte> version = line[(sp2 + 1)..];
            bool http10 = version.SequenceEqual("HTTP/1.0"u8);
            if (!http10 && !version.SequenceEqual("HTTP/1.1"u8))
            {
                Fail(o, ref flags);
                return off;
            }
            int q = path.IndexOf((byte)'?');
            if (q >= 0) path = path[..q];

            int mode = http10 ? Api.Close : Api.Default;
            ReadOnlySpan<byte> auth = default;
            long contentLength = 0;
            bool expect = false, bad = false;
            ReadOnlySpan<byte> headers = headEnd > lineEnd ? rest[(lineEnd + 2)..(headEnd + 2)] : default;
            while (!headers.IsEmpty)
            {
                int e = headers.IndexOf("\r\n"u8);
                ReadOnlySpan<byte> h = headers[..e];
                headers = headers[(e + 2)..];
                int colon = h.IndexOf((byte)':');
                if (colon <= 0) continue;
                ReadOnlySpan<byte> name = h[..colon];
                ReadOnlySpan<byte> value = h[(colon + 1)..].Trim(" \t"u8);
                switch (name.Length)
                {
                    case 13 when Ascii.EqualsIgnoreCase(name, "authorization"u8):
                        auth = value;
                        break;
                    case 14 when Ascii.EqualsIgnoreCase(name, "content-length"u8):
                        if (value.IsEmpty || value.Length > 9) bad = true;
                        contentLength = 0;
                        foreach (byte c in value)
                        {
                            if (c is < (byte)'0' or > (byte)'9') bad = true;
                            contentLength = contentLength * 10 + (c - '0');
                        }
                        break;
                    case 10 when Ascii.EqualsIgnoreCase(name, "connection"u8):
                        if (Ascii.EqualsIgnoreCase(value, "close"u8)) mode = Api.Close;
                        else if (http10 && Ascii.EqualsIgnoreCase(value, "keep-alive"u8)) mode = Api.KeepAlive;
                        break;
                    case 17 when Ascii.EqualsIgnoreCase(name, "transfer-encoding"u8):
                        bad = true; // chunked bodies: not supported
                        break;
                    case 6 when Ascii.EqualsIgnoreCase(name, "expect"u8):
                        expect = Ascii.EqualsIgnoreCase(value, "100-continue"u8);
                        break;
                }
            }
            if (bad || contentLength > MaxBody)
            {
                Fail(o, ref flags);
                return off;
            }
            int total = headEnd + 4 + (int)contentLength;
            if (rest.Length < total)
            {
                if (expect && (flags & ContinueSent) == 0)
                {
                    o.Write("HTTP/1.1 100 Continue\r\n\r\n"u8);
                    flags |= ContinueSent;
                }
                return off;
            }
            bool post = method.SequenceEqual("POST"u8);
            if (post && Db.HoldWrites)
            {
                flags |= Held;
                return off;
            }
            try
            {
                Route(o, mode, post, method, path, auth, rest.Slice(headEnd + 4, (int)contentLength));
            }
            catch (Exception)
            {
                Json.Clear();
                Api.Respond(o, 500, mode, Api.Internal);
            }
            off += total;
            flags &= ~ContinueSent; // a pipelined request gets its own 100 Continue
            if (mode == Api.Close)
            {
                flags |= Close;
                return off;
            }
        }
    }

    /// <summary>A request this server cannot read: 400, and the connection closes.</summary>
    static void Fail(Arena o, ref int flags)
    {
        Api.Respond(o, 400, Api.Close, BadRequest);
        flags |= Close;
    }

    static void Route(Arena o, int mode, bool post, ReadOnlySpan<byte> method, ReadOnlySpan<byte> path,
        ReadOnlySpan<byte> auth, ReadOnlySpan<byte> body)
    {
        if (!post)
        {
            if (!method.SequenceEqual("GET"u8)) Api.Respond(o, 404, mode, Api.NotFound);
            else if (path.SequenceEqual("/feed"u8)) Api.Feed(o, mode);
            else if (path.StartsWith("/posts/"u8) && IsSegment(path[7..]))
            {
                long id = ParseId(path[7..]);
                if (id == 0) Api.Respond(o, 400, mode, Api.InvalidId);
                else if (id < 0) Api.Respond(o, 404, mode, Api.PostNotFound);
                else Api.GetPost(o, mode, id);
            }
            else if (path.SequenceEqual("/health"u8)) Api.Health(o, mode);
            else Api.Respond(o, 404, mode, Api.NotFound);
            return;
        }
        bool create = path.SequenceEqual("/posts"u8);
        bool like = !create && path.Length > 12 && path.StartsWith("/posts/"u8) && path.EndsWith("/like"u8)
            && IsSegment(path[7..^5]);
        if (!create && !like)
        {
            Api.Respond(o, 404, mode, Api.NotFound);
            return;
        }
        // auth first, then the id (spec)
        Auth.Result a = Auth.Verify(auth, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out long userId, out ReadOnlySpan<byte> username);
        if (a != Auth.Result.Ok)
        {
            Api.Respond(o, 401, mode, Auth.Message(a));
            return;
        }
        if (create)
        {
            Api.CreatePost(o, mode, userId, username, body);
            return;
        }
        long postId = ParseId(path[7..^5]);
        if (postId == 0) Api.Respond(o, 400, mode, Api.InvalidId);
        else if (postId < 0) Api.Respond(o, 404, mode, Api.PostNotFound);
        else Api.Like(o, mode, userId, postId);
    }

    static bool IsSegment(ReadOnlySpan<byte> s) => !s.IsEmpty && s.IndexOf((byte)'/') < 0;

    /// <summary>A post id: ASCII digits above zero. 0 when it is not a positive integer, -1 when it is one too big
    /// for any row (more than 18 digits).</summary>
    public static long ParseId(ReadOnlySpan<byte> s)
    {
        long v = 0;
        int digits = 0;
        foreach (byte c in s)
        {
            if (c is < (byte)'0' or > (byte)'9') return 0;
            if (digits > 0 || c != '0') digits++;
            if (digits > 18) v = -1;
            else v = v * 10 + (c - '0');
        }
        return v;
    }
}
