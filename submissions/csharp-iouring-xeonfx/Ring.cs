using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>One completion: user_data, result, flags.</summary>
[StructLayout(LayoutKind.Sequential)]
struct Cqe
{
    public ulong UserData;
    public int Res;
    public uint Flags;
}

/// <summary>A connection's state in its ring.</summary>
struct Conn
{
    /// <summary>Changes when the slot closes, so completions still in flight for the old socket are recognized.</summary>
    public uint Gen;
    public byte State;
    /// <summary>The start of a request that is not complete yet (or that waits for a hold to end).</summary>
    public byte[]? In;
    public int InLength;
    /// <summary>Pinned: the bytes of the send in flight from the connection's own buffer.</summary>
    public byte[]? Own;
    public int OwnOffset, OwnLength;
    /// <summary>Responses made while that send is in flight.</summary>
    public byte[]? Pending;
    public int PendingLength;
    /// <summary>Pass number + 1 of the connection's response record in the arena, and its index.</summary>
    public int RecordPass, Record;
}

/// <summary>
/// An io_uring instance, used through raw syscalls: the submission and completion rings, a table of fixed files and
/// a ring of provided receive buffers.
/// <para>Accepted sockets go straight into the table (multishot accept with IORING_FILE_INDEX_ALLOC): they never
/// get a process fd, so LimitNOFILE does not cap the connections, only the size of one table. Each socket has one
/// multishot receive into the buffer ring.</para>
/// <para>SINGLE_ISSUER | DEFER_TASKRUN: completion work runs only when the loop thread enters the ring.</para>
/// </summary>
sealed unsafe class Ring
{
    // io_uring_setup flags and features
    const uint SetupCqSize = 1 << 3, SetupSubmitAll = 1 << 7, SetupCoopTaskrun = 1 << 8;
    const uint SetupSingleIssuer = 1 << 12, SetupDeferTaskrun = 1 << 13;
    const uint FeatSingleMmap = 1, FeatExtArg = 1 << 8;
    public const long EnterGetEvents = 1, EnterExtArg = 8;
    const long RegisterFiles2 = 13, RegisterPbufRing = 22;
    // opcodes
    const byte OpAccept = 13, OpAsyncCancel = 14, OpClose = 19, OpSend = 26, OpRecv = 27, OpProvideBuffers = 31, OpShutdown = 34;
    const byte OpUringCmd = 46;
    const uint SocketCmdSetsockopt = 3, IpProtoTcp = 6, TcpQuickAck = 12;
    // SQE flags
    const byte SqeFixedFile = 1, SqeIoHardlink = 1 << 3, SqeBufferSelect = 1 << 5, SqeCqeSkipSuccess = 1 << 6;
    const ushort AcceptMultishot = 1, RecvMultishot = 2;
    const uint FileIndexAlloc = 0xFFFFFFFF;
    public const uint CqeFBuffer = 1, CqeFMore = 2;
    public const uint MsgDontWait = 0x40, MsgNoSignal = 0x4000;

    const uint SqEntries = 4096, CqEntries = 16384;
    /// <summary>Provided receive buffers: a request is a few hundred bytes, a bigger one arrives in parts. A loop
    /// that falls behind takes many at once (the receives complete in one enter), and a receive with no buffer fails
    /// and must be armed again, so there are plenty.</summary>
    public const int BufferSize = 2048;
    public static readonly int BufferCount = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(Loop.Knob("RECV_BUFFERS", 8192), 64, 32768));
    const ushort BufferGroup = 0;

    // user_data: op (3 bits) | ring (3) | slot (16) | gen (24) | extra (18)
    public const ulong UdAccept = 1, UdRecv = 2, UdSendArena = 3, UdSendOwn = 4, UdIgnore = 5;

    public static ulong Ud(ulong op, int ring, int slot, uint gen, int extra) =>
        op | (ulong)ring << 3 | (ulong)slot << 6 | (ulong)(gen & 0xFFFFFF) << 22 | (ulong)extra << 46;

    public static int UdRing(ulong u) => (int)(u >> 3 & 7);
    public static int UdSlot(ulong u) => (int)(u >> 6 & 0xFFFF);
    public static uint UdGen(ulong u) => (uint)(u >> 22) & 0xFFFFFF;
    public static int UdExtra(ulong u) => (int)(u >> 46);

    // slot states
    public const byte Live = 1, Receiving = 2, Sending = 4, CloseAfterSend = 8, Done = 16, HeldWrite = 32;

    public readonly int Index, Fd, Slots;
    public readonly Conn[] Conns;
    public int Open;
    /// <summary>A bit for each listener with a multishot accept armed on this ring.</summary>
    public uint Accepting;
    readonly uint[] _acceptGens = new uint[32];

    readonly uint* _sqHead, _sqTail, _cqHead, _cqTail;
    readonly uint _sqMask, _cqMask, _sqEntries;
    readonly byte* _sqes;
    readonly Cqe* _cqes;
    uint _tail, _published;

    readonly byte* _bufRing, _bufMem;
    ushort _bufTail, _publishedTail;
    /// <summary>The kernel refused the buffer ring: buffers go back with IORING_OP_PROVIDE_BUFFERS.</summary>
    readonly bool _legacy;
    readonly List<int> _returned = new();

    public Ring(int index, int slots)
    {
        Index = index;
        Slots = slots;
        byte* p = stackalloc byte[120];
        new Span<byte>(p, 120).Clear();
        *(uint*)(p + 4) = CqEntries;
        *(uint*)(p + 8) = SetupCqSize | SetupSubmitAll | SetupCoopTaskrun | SetupSingleIssuer | SetupDeferTaskrun;
        long fd = Native.syscall(Native.SysIoUringSetup, SqEntries, (long)p, 0, 0, 0, 0);
        if (fd < 0) throw new InvalidOperationException($"io_uring_setup: errno {Native.Errno}");
        Fd = (int)fd;
        uint features = *(uint*)(p + 20);
        if ((features & FeatSingleMmap) == 0 || (features & FeatExtArg) == 0)
            throw new InvalidOperationException("io_uring: kernel too old (needs SINGLE_MMAP and EXT_ARG)");
        _sqEntries = *(uint*)p;
        uint cqEntries = *(uint*)(p + 4);
        // io_sqring_offsets at 40: head, tail, ring_mask, ring_entries, flags, dropped, array; io_cqring_offsets at
        // 80: head, tail, ring_mask, ring_entries, overflow, cqes
        uint* sq = (uint*)(p + 40), cq = (uint*)(p + 80);
        nuint size = Math.Max(sq[6] + _sqEntries * 4, cq[5] + cqEntries * 16);
        byte* ring = (byte*)Native.mmap(null, size, Native.ProtRead | Native.ProtWrite, Native.MapShared | Native.MapPopulate, Fd, 0);
        if (ring == (byte*)-1) throw new InvalidOperationException($"io_uring mmap: errno {Native.Errno}");
        _sqes = (byte*)Native.mmap(null, _sqEntries * 64, Native.ProtRead | Native.ProtWrite, Native.MapShared | Native.MapPopulate, Fd, 0x10000000);
        if (_sqes == (byte*)-1) throw new InvalidOperationException($"io_uring mmap sqes: errno {Native.Errno}");
        _sqHead = (uint*)(ring + sq[0]);
        _sqTail = (uint*)(ring + sq[1]);
        _sqMask = *(uint*)(ring + sq[2]);
        uint* array = (uint*)(ring + sq[6]);
        for (uint i = 0; i < _sqEntries; i++) array[i] = i;
        _cqHead = (uint*)(ring + cq[0]);
        _cqTail = (uint*)(ring + cq[1]);
        _cqMask = *(uint*)(ring + cq[2]);
        _cqes = (Cqe*)(ring + cq[5]);
        _tail = _published = *_sqTail;

        // the table of fixed files, empty (IORING_RSRC_REGISTER_SPARSE)
        byte* reg = stackalloc byte[40];
        new Span<byte>(reg, 40).Clear();
        *(uint*)reg = (uint)slots;
        *(uint*)(reg + 4) = 1;
        Register(RegisterFiles2, reg, 32);
        Conns = new Conn[slots];

        _bufRing = MapAnonymous(BufferCount * 16);
        _bufMem = MapAnonymous(BufferCount * BufferSize);
        new Span<byte>(reg, 40).Clear();
        *(ulong*)reg = (ulong)_bufRing;
        *(uint*)(reg + 8) = (uint)BufferCount;
        *(ushort*)(reg + 12) = BufferGroup;
        // Ubuntu's 6.8.0-147 answers EINVAL to every buffer ring (7.0 takes them)
        _legacy = Native.syscall(Native.SysIoUringRegister, Fd, RegisterPbufRing, (long)reg, 1, 0, 0) < 0;
        if (_legacy)
        {
            int errno = Native.Errno; // before Console.Error, whose first use makes calls of its own
            if (index == 0) Console.Error.WriteLine($"io_uring buffer ring refused (errno {errno}): using IORING_OP_PROVIDE_BUFFERS");
            Provide(0, BufferCount);
        }
        else
        {
            for (int bid = 0; bid < BufferCount; bid++) Recycle(bid);
            PublishBuffers();
        }
    }

    static byte* MapAnonymous(long length)
    {
        byte* m = (byte*)Native.mmap(null, (nuint)length, Native.ProtRead | Native.ProtWrite, Native.MapPrivate | Native.MapAnonymous, -1, 0);
        if (m == (byte*)-1) throw new OutOfMemoryException($"mmap {length} bytes: errno {Native.Errno}");
        return m;
    }

    void Register(long opcode, void* arg, long count)
    {
        if (Native.syscall(Native.SysIoUringRegister, Fd, opcode, (long)arg, count, 0, 0) < 0)
            throw new InvalidOperationException($"io_uring_register {opcode}: errno {Native.Errno}");
    }

    public byte* Buffer(int bid) => _bufMem + (long)bid * BufferSize;

    /// <summary>Gives a receive buffer back to the kernel (seen by it after <see cref="PublishBuffers"/>).</summary>
    public void Recycle(int bid)
    {
        if (_legacy)
        {
            _returned.Add(bid);
            return;
        }
        byte* e = _bufRing + (_bufTail & (BufferCount - 1)) * 16;
        *(ulong*)e = (ulong)Buffer(bid);
        *(uint*)(e + 8) = BufferSize;
        *(ushort*)(e + 12) = (ushort)bid;
        _bufTail++;
    }

    /// <summary>Buffers given back since the last <see cref="PublishBuffers"/>.</summary>
    public int Returned => _legacy ? _returned.Count : (ushort)(_bufTail - _publishedTail);

    public void PublishBuffers()
    {
        if (!_legacy)
        {
            _publishedTail = _bufTail;
            // the tail is the last u16 of entry 0
            Volatile.Write(ref *(ushort*)(_bufRing + 14), _bufTail);
            return;
        }
        // one PROVIDE_BUFFERS for each run of consecutive ids
        for (int i = 0; i < _returned.Count;)
        {
            int first = _returned[i], n = 1;
            while (i + n < _returned.Count && _returned[i + n] == first + n) n++;
            Provide(first, n);
            i += n;
        }
        _returned.Clear();
    }

    void Provide(int first, int count)
    {
        byte* s = Sqe();
        s[0] = OpProvideBuffers;
        s[1] = SqeCqeSkipSuccess;
        *(int*)(s + 4) = count;
        *(ulong*)(s + 8) = (ulong)first;
        *(ulong*)(s + 16) = (ulong)Buffer(first);
        *(uint*)(s + 24) = BufferSize;
        *(ulong*)(s + 32) = UdIgnore;
        *(ushort*)(s + 40) = BufferGroup;
    }

    /// <summary>The next SQE, zeroed. Submits first when the queue is full.</summary>
    byte* Sqe()
    {
        if (_tail - Volatile.Read(ref *_sqHead) >= _sqEntries) Submit();
        byte* s = _sqes + (_tail & _sqMask) * 64;
        new Span<byte>(s, 64).Clear();
        _tail++;
        return s;
    }

    public uint Unsubmitted => _tail - _published;

    /// <summary>io_uring_enter: submits the queued SQEs and, with <paramref name="wait"/> above 0, waits for that many
    /// completions (or the timeout in <paramref name="arg"/>).</summary>
    public long Enter(uint wait, long flags, void* arg)
    {
        Volatile.Write(ref *_sqTail, _tail);
        uint n = _tail - _published;
        _published = _tail;
        long argSize = (flags & EnterExtArg) != 0 ? 24 : 0;
        return wait == 0
            ? Native.syscall_fast(Native.SysIoUringEnter, Fd, n, 0, flags, (long)arg, argSize)
            : Native.syscall(Native.SysIoUringEnter, Fd, n, wait, flags, (long)arg, argSize);
    }

    /// <summary>Submits, and runs the ring's completion work (DEFER_TASKRUN runs it only inside an enter).</summary>
    public void Submit() => Enter(0, EnterGetEvents, null);

    /// <summary>Moves up to <paramref name="cap"/> ready completions into <paramref name="into"/>.</summary>
    public int Reap(List<Cqe> into, int cap)
    {
        uint head = *_cqHead;
        uint tail = Volatile.Read(ref *_cqTail);
        int n = 0;
        while (head != tail && n < cap)
        {
            into.Add(_cqes[head & _cqMask]);
            head++;
            n++;
        }
        Volatile.Write(ref *_cqHead, head);
        return n;
    }

    /// <summary>Arms a multishot accept for each listener that has none. Its user_data carries the listener's
    /// index where a connection's carries the slot.</summary>
    public void Accept(int[] listeners)
    {
        for (int i = 0; i < listeners.Length; i++)
        {
            if ((Accepting >> i & 1) != 0) continue;
            byte* s = Sqe();
            s[0] = OpAccept;
            *(ushort*)(s + 2) = AcceptMultishot;
            *(int*)(s + 4) = listeners[i];
            *(ulong*)(s + 32) = Ud(UdAccept, Index, i, ++_acceptGens[i], 0);
            *(uint*)(s + 44) = FileIndexAlloc;
            Accepting |= 1u << i;
        }
    }

    /// <summary>A multishot accept ended (its completion had no MORE flag): true when it is the one armed now, not
    /// one cancelled earlier.</summary>
    public bool AcceptEnded(ulong userData)
    {
        int i = UdSlot(userData);
        if (UdGen(userData) != (_acceptGens[i] & 0xFFFFFF) || (Accepting >> i & 1) == 0) return false;
        Accepting &= ~(1u << i);
        return true;
    }

    public void StopAccept()
    {
        for (int i = 0; Accepting != 0; i++)
        {
            if ((Accepting >> i & 1) == 0) continue;
            byte* s = Sqe();
            s[0] = OpAsyncCancel;
            s[1] = SqeCqeSkipSuccess;
            *(int*)(s + 4) = -1;
            *(ulong*)(s + 16) = Ud(UdAccept, Index, i, _acceptGens[i], 0); // the user_data to cancel
            *(ulong*)(s + 32) = UdIgnore;
            Accepting &= ~(1u << i);
        }
    }

    public void Recv(int slot)
    {
        byte* s = Sqe();
        s[0] = OpRecv;
        s[1] = SqeFixedFile | SqeBufferSelect;
        *(ushort*)(s + 2) = RecvMultishot;
        *(int*)(s + 4) = slot;
        *(ulong*)(s + 32) = Ud(UdRecv, Index, slot, Conns[slot].Gen, 0);
        *(ushort*)(s + 40) = BufferGroup;
        Conns[slot].State |= Receiving;
    }

    /// <summary>The int 0 that <see cref="DelayAcks"/> passes to setsockopt.</summary>
    static readonly int* Zero = (int*)NativeMemory.AllocZeroed(4);

    /// <summary>setsockopt(TCP_QUICKACK, 0) on a fixed file, through io_uring (kernel 6.7 and up; an older kernel
    /// fails it and nothing changes). The socket then acks a request with its response: a request after an idle gap
    /// would otherwise get an ACK of its own, one more packet out for each request.</summary>
    public void DelayAcks(int slot)
    {
        byte* s = Sqe();
        s[0] = OpUringCmd;
        s[1] = SqeFixedFile | SqeCqeSkipSuccess;
        *(int*)(s + 4) = slot;
        *(uint*)(s + 8) = SocketCmdSetsockopt;
        *(uint*)(s + 16) = IpProtoTcp;
        *(uint*)(s + 20) = TcpQuickAck;
        *(ulong*)(s + 32) = UdIgnore;
        *(uint*)(s + 44) = 4;
        *(ulong*)(s + 48) = (ulong)Zero;
    }

    public void Send(ulong op, int slot, byte* address, int length, uint msgFlags, int extra)
    {
        byte* s = Sqe();
        s[0] = OpSend;
        s[1] = SqeFixedFile;
        *(int*)(s + 4) = slot;
        *(ulong*)(s + 16) = (ulong)address;
        *(uint*)(s + 24) = (uint)length;
        *(uint*)(s + 28) = msgFlags;
        *(ulong*)(s + 32) = Ud(op, Index, slot, Conns[slot].Gen, extra);
    }

    /// <summary>SHUTDOWN (sends the FIN now and ends the multishot receive) hard-linked to the CLOSE of the fixed
    /// file. On kernel 6.8 a closed fixed file is released only after every older request on the ring finished, and
    /// the multishot receives of the other sockets never do: the CLOSE alone would send the FIN much later.</summary>
    public void ShutdownClose(int slot)
    {
        byte* s = Sqe();
        s[0] = OpShutdown;
        s[1] = SqeFixedFile | SqeIoHardlink | SqeCqeSkipSuccess;
        *(int*)(s + 4) = slot;
        *(uint*)(s + 24) = 2; // SHUT_RDWR
        *(ulong*)(s + 32) = UdIgnore;
        byte* c = Sqe();
        c[0] = OpClose;
        c[1] = SqeCqeSkipSuccess;
        *(uint*)(c + 44) = (uint)slot + 1;
        *(ulong*)(c + 32) = UdIgnore;
    }
}
