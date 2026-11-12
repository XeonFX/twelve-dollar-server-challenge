using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>A growing pinned byte buffer. The loop writes the responses of one pass into one arena, and the kernel
/// sends straight from it; the JSON body of the response being written goes into another.</summary>
sealed unsafe class Arena(int initial)
{
    byte[] _buf = GC.AllocateUninitializedArray<byte>(initial, pinned: true);

    public byte[] Array => _buf;
    public int Length;

    public byte* Address(int offset) => (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_buf)) + offset;

    public ReadOnlySpan<byte> Written => new(_buf, 0, Length);

    /// <summary>Room for at least <paramref name="n"/> more bytes, at <see cref="Length"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> Reserve(int n)
    {
        if (_buf.Length - Length < n) Grow(n);
        return new Span<byte>(_buf, Length, _buf.Length - Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void Grow(int n)
    {
        var bigger = GC.AllocateUninitializedArray<byte>(Math.Max(_buf.Length * 2, Length + n), pinned: true);
        _buf.AsSpan(0, Length).CopyTo(bigger);
        _buf = bigger;
    }

    /// <summary>Room for at least <paramref name="n"/> more bytes: the address of the first, valid until the arena
    /// grows.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* Room(int n)
    {
        if (_buf.Length - Length < n) Grow(n);
        return Address(Length);
    }

    public void Write(ReadOnlySpan<byte> s)
    {
        s.CopyTo(Reserve(s.Length));
        Length += s.Length;
    }

    public void Put(byte b)
    {
        if (Length == _buf.Length) Grow(1);
        _buf[Length++] = b;
    }

    /// <summary>Moves <paramref name="length"/> bytes from <paramref name="start"/> to the end.</summary>
    public void MoveToEnd(int start, int length)
    {
        Reserve(length);
        _buf.AsSpan(start, length).CopyTo(_buf.AsSpan(Length));
        Length += length;
    }
}
