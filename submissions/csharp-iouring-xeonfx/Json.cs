using System;
using System.Buffers;
using System.Buffers.Text;

/// <summary>The JSON body of the response being written. Keys go in as fixed bytes and values as SQLite's UTF-8
/// bytes, with only the escapes JSON requires (escaping &lt;&gt;&amp;/ and non-ASCII is optional in the spec).</summary>
static class Json
{
    public static readonly Arena Body = new(16384);

    /// <summary>The bytes a JSON string must escape: the quote, the backslash and the control characters.</summary>
    static readonly SearchValues<byte> Special = SearchValues.Create(
        "\"\\\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\b\t\n\u000b\f\r\u000e\u000f\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001a\u001b\u001c\u001d\u001e\u001f"u8);

    public static void Clear() => Body.Length = 0;

    public static void Raw(ReadOnlySpan<byte> s) => Body.Write(s);

    public static void Put(byte b) => Body.Put(b);

    public static void Num(long v)
    {
        Utf8Formatter.TryFormat(v, Body.Reserve(20), out int n);
        Body.Length += n;
    }

    /// <summary>A JSON string, quotes included, from UTF-8 bytes. Bytes from 0x80 up pass unchanged.</summary>
    public static void Str(ReadOnlySpan<byte> s)
    {
        Arena b = Body;
        b.Put((byte)'"');
        int i = s.IndexOfAny(Special);
        while (i >= 0)
        {
            b.Write(s[..i]);
            Escape(s[i]);
            s = s[(i + 1)..];
            i = s.IndexOfAny(Special);
        }
        b.Write(s);
        b.Put((byte)'"');
    }

    static void Escape(byte c)
    {
        Span<byte> d = Body.Reserve(6);
        d[0] = (byte)'\\';
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
        if (e != 0)
        {
            d[1] = e;
            Body.Length += 2;
            return;
        }
        d[1] = (byte)'u';
        d[2] = (byte)'0';
        d[3] = (byte)'0';
        d[4] = (byte)"0123456789abcdef"[c >> 4];
        d[5] = (byte)"0123456789abcdef"[c & 15];
        Body.Length += 6;
    }
}
