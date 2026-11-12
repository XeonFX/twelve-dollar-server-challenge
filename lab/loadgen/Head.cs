using System;
using System.Buffers.Text;
using System.Text;

/// <summary>What the load generator reads from a response head (the bytes before the blank line).</summary>
static class ResponseHead
{
    /// <summary>The status code and the body's length (0 without a Content-Length header). The header's name in
    /// any case, and spaces or tabs around its value, as HTTP allows: the entry right-aligns the digits. False
    /// when the head is not an HTTP/1.1 response or the length is not a number an int holds.</summary>
    public static bool TryParse(ReadOnlySpan<byte> head, out int status, out int contentLength)
    {
        status = 0;
        contentLength = 0;
        if (head.Length < 12 || !head.StartsWith("HTTP/1.1 "u8)) return false;
        for (int i = 9; i < 12; i++)
        {
            if (!IsDigit(head[i])) return false;
            status = status * 10 + (head[i] - '0');
        }
        ReadOnlySpan<byte> name = "content-length:"u8;
        int lineEnd = head.IndexOf("\r\n"u8);
        while (lineEnd >= 0)
        {
            head = head[(lineEnd + 2)..];
            lineEnd = head.IndexOf("\r\n"u8);
            ReadOnlySpan<byte> line = lineEnd < 0 ? head : head[..lineEnd];
            if (line.Length < name.Length || !Ascii.EqualsIgnoreCase(line[..name.Length], name)) continue;
            ReadOnlySpan<byte> value = line[name.Length..].Trim(" \t"u8);
            if (value.Length == 0) return false;
            long length = 0;
            foreach (byte digit in value)
            {
                if (!IsDigit(digit)) return false;
                length = length * 10 + (digit - '0');
                if (length > int.MaxValue) return false;
            }
            contentLength = (int)length;
            return true;
        }
        return true;
    }

    static bool IsDigit(byte b) => (uint)(b - '0') <= 9;

    /// <summary>The ids of a feed's posts, in order, into <paramref name="ids"/>: how many it found.</summary>
    public static int PostIds(ReadOnlySpan<byte> body, Span<long> ids)
    {
        int count = 0, at = 0;
        while (count < ids.Length)
        {
            int i = body[at..].IndexOf("{\"id\":"u8);
            if (i < 0) break;
            at += i + 6;
            if (!Utf8Parser.TryParse(body[at..], out long id, out int used)) break;
            ids[count++] = id;
            at += used;
        }
        return count;
    }
}
