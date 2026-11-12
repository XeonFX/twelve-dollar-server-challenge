using System;
using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;

/// <summary>Writes JSON straight into the response being built (<see cref="Out"/>, set by <see cref="Api"/>). Keys
/// go in as fixed bytes and values as SQLite's UTF-8 bytes, with only the escapes JSON requires (escaping
/// &lt;&gt;&amp;/ and non-ASCII is optional in the spec).</summary>
static unsafe class Json
{
    /// <summary>Where the JSON goes: the arena of the pass's responses, after the response's head.</summary>
    public static Arena Out = new(16384);

    /// <summary>The bytes a JSON string must escape: the quote, the backslash and the control characters.</summary>
    static readonly SearchValues<byte> Special = SearchValues.Create(
        "\"\\\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\b\t\n\u000b\f\r\u000e\u000f\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001a\u001b\u001c\u001d\u001e\u001f"u8);

    public static void Raw(ReadOnlySpan<byte> s) => Out.Write(s);

    public static void Put(byte b) => Out.Put(b);

    public static void Num(long v)
    {
        byte* p = Out.Room(20);
        Out.Length += (int)(Num(p, v) - p);
    }

    /// <summary>A JSON string, quotes included, from UTF-8 bytes. Bytes from 0x80 up pass unchanged.</summary>
    public static void Str(ReadOnlySpan<byte> s)
    {
        byte* p = Out.Room(2 + 6 * s.Length);
        fixed (byte* src = s)
            Out.Length += (int)(Str(p, src, s.Length) - p);
    }

    /// <summary>One post object, in the spec's key order, written in one piece.</summary>
    public static void Post(long id, byte* body, int bodyLength, byte* created, int createdLength, byte* author, int authorLength, long likes)
    {
        Arena o = Out;
        // the fixed text and two numbers, and every byte of a string as \u00XX at worst
        byte* start = o.Room(128 + 6 * (bodyLength + createdLength + authorLength));
        byte* p = Lit(start, "{\"id\":"u8);
        p = Num(p, id);
        p = Lit(p, ",\"body\":"u8);
        p = Str(p, body, bodyLength);
        p = Lit(p, ",\"created_at\":"u8);
        p = Str(p, created, createdLength);
        p = Lit(p, ",\"author\":"u8);
        p = Str(p, author, authorLength);
        p = Lit(p, ",\"like_count\":"u8);
        p = Num(p, likes);
        *p++ = (byte)'}';
        o.Length += (int)(p - start);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static byte* Lit(byte* p, ReadOnlySpan<byte> text)
    {
        text.CopyTo(new Span<byte>(p, text.Length));
        return p + text.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static byte* Num(byte* p, long v)
    {
        if ((ulong)v < 10)
        {
            *p = (byte)('0' + v);
            return p + 1;
        }
        Utf8Formatter.TryFormat(v, new Span<byte>(p, 20), out int n);
        return p + n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static byte* Str(byte* p, byte* s, int length)
    {
        *p++ = (byte)'"';
        var text = new ReadOnlySpan<byte>(s, length);
        int i = text.IndexOfAny(Special);
        if (i < 0)
        {
            text.CopyTo(new Span<byte>(p, length));
            p += length;
        }
        else p = Escaped(p, text, i);
        *p++ = (byte)'"';
        return p;
    }

    /// <summary>A string whose byte at <paramref name="i"/> is the first that needs an escape.</summary>
    static byte* Escaped(byte* p, ReadOnlySpan<byte> s, int i)
    {
        while (i >= 0)
        {
            s[..i].CopyTo(new Span<byte>(p, i));
            p += i;
            byte c = s[i];
            *p++ = (byte)'\\';
            byte e = c switch
            {
                (byte)'"' => (byte)'"',
                (byte)'\\' => (byte)'\\',
                (byte)'\n' => (byte)'n',
                (byte)'\r' => (byte)'r',
                (byte)'\t' => (byte)'t',
                (byte)'\b' => (byte)'b',
                (byte)'\f' => (byte)'f',
                _ => 0,
            };
            if (e != 0) *p++ = e;
            else
            {
                p[0] = (byte)'u';
                p[1] = (byte)'0';
                p[2] = (byte)'0';
                p[3] = (byte)"0123456789abcdef"[c >> 4];
                p[4] = (byte)"0123456789abcdef"[c & 15];
                p += 5;
            }
            s = s[(i + 1)..];
            i = s.IndexOfAny(Special);
        }
        s.CopyTo(new Span<byte>(p, s.Length));
        return p + s.Length;
    }
}
