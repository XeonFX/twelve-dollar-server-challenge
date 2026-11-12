using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// The one thread that owns every ring and runs every request, so the database needs no lock. A pass:
/// <list type="number">
/// <item>Waits in ring 0 for 256 completions or 8 ms, so one pass serves many sockets, and runs the other rings'
/// completion work.</item>
/// <item>Handles the completions: new connections, received requests. Each response goes into one arena.</item>
/// <item>Commits the pass transaction (group commit), then queues one send per connection with MSG_DONTWAIT. The sends
/// finish inside the submitting io_uring_enter; a response the socket did not take whole goes on from the
/// connection's own buffer.</item>
/// </list>
/// A ring's table holds at most the fd limit (65,535) sockets; accept moves to the next ring before one fills up,
/// up to 8 rings.
/// </summary>
sealed unsafe class Loop
{
    const int MaxRings = 8;

    /// <summary>A setting from the environment, for testing; <paramref name="fallback"/> when it is not set.</summary>
    public static int Knob(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int v) && v >= 0 ? v : fallback;

    /// <summary>Completions handled in one pass at most (the rest wait, in order, for the next one), about 40 ms of
    /// work. Responses go out only at the end of a pass, so an uncapped pass after a stall would hold every request
    /// that came meanwhile, take as long again, and keep the loop in passes of a second or more.</summary>
    static readonly int PassCap = Math.Max(1, Knob("PASS_CAP", 1024));
    /// <summary>A pass starts when this many completions wait, or after <see cref="WaitNanos"/>.</summary>
    static readonly uint WaitCount = (uint)Math.Max(1, Knob("WAIT_COUNT", 256));
    static readonly long WaitNanos = Math.Max(1, Knob("WAIT_US", 8000)) * 1000L;
    /// <summary>Writes run, and the transaction commits, at most once in this long (0: in every pass). The passes
    /// between read in the transaction that is open: nothing else writes, so they read what is committed, and
    /// their responses go out at once.</summary>
    static readonly long WindowTicks = Knob("WINDOW_MS", 0) * Stopwatch.Frequency / 1000;
    /// <summary>A connection state bit beside <see cref="Ring"/>'s: its next request already got 100 Continue.</summary>
    const byte ContinueSent = 64;
    /// <summary>The bits of a user_data that name a (ring, slot, generation).</summary>
    const ulong KeyMask = 0x3FFFFFFFFFF8;

    struct Record
    {
        public int Ring, Slot, Start, Length;
        public uint Gen;
        public bool Close;
    }

    readonly int[] _listeners;
    readonly int _slots;
    readonly List<Ring> _rings = new(MaxRings);
    /// <summary>The responses of one pass; the kernel sends from it before the next pass.</summary>
    readonly Arena _out = new(1 << 20);
    Record[] _records = new Record[1024];
    int _recordCount;
    readonly int[] _arenaSends = new int[MaxRings];
    readonly List<Cqe> _cqes = new(PassCap);
    readonly List<Cqe> _carry = new(256);
    /// <summary>Receives that found no free buffer, to arm again as buffers come back.</summary>
    readonly Queue<(int Ring, int Slot, uint Gen)> _rearm = new();
    /// <summary>Connections whose next request is a write that waits for <see cref="Db.WritesOpen"/>.</summary>
    readonly List<(int Ring, int Slot, uint Gen)> _held = new();
    /// <summary>Buffers of sends still in flight for connections that closed meanwhile.</summary>
    readonly Dictionary<ulong, byte[]> _zombies = new();
    readonly byte* _waitArg;
    int _pass, _acceptRing;
    long _acceptRetryAt, _windowAt;

    public Loop(int[] listeners)
    {
        _listeners = listeners;
        _slots = Native.MaxFiles();
        // io_uring_getevents_arg (sigmask, sigmask_sz, pad, ts) and the __kernel_timespec it points to
        _waitArg = (byte*)NativeMemory.AllocZeroed(40);
        *(ulong*)(_waitArg + 16) = (ulong)(_waitArg + 24);
        *(long*)(_waitArg + 32) = WaitNanos;
        AddRing();
    }

    Ring AddRing()
    {
        var r = new Ring(_rings.Count, _slots);
        _rings.Add(r);
        return r;
    }

    public void Run()
    {
        _rings[0].Accept(_listeners);
        _rings[0].Submit();
        int open = 0;
        while (true)
        {
            // 1. wait: for WaitCount completions or WaitNanos while connections are open, else for the first one
            Ring r0 = _rings[0];
            if (_carry.Count > 0) r0.Submit();
            else if (open == 0 && _rings.Count == 1 && r0.Accepting != 0)
            {
                // nothing may come for a long time: no transaction stays open meanwhile
                if (Db.InTxn) Db.Commit();
                r0.Enter(1, Ring.EnterGetEvents, null);
            }
            else r0.Enter(WaitCount, Ring.EnterGetEvents | Ring.EnterExtArg, _waitArg);
            for (int i = 1; i < _rings.Count; i++) _rings[i].Submit();

            // 2. handle the completions
            _pass++;
            Api.Tick();
            long now = Stopwatch.GetTimestamp();
            Db.CheckpointStep(now / (Stopwatch.Frequency / 1000));
            // writes run only in a pass that commits: once per window, and not while a checkpoint holds them
            bool window = !Db.HoldWrites && now >= _windowAt;
            Db.WritesOpen = window;
            if (window && _held.Count > 0) ResumeHeld();
            _cqes.Clear();
            int carried = Math.Min(_carry.Count, PassCap);
            for (int i = 0; i < carried; i++) _cqes.Add(_carry[i]);
            _carry.RemoveRange(0, carried);
            int room = PassCap - _cqes.Count;
            foreach (Ring r in _rings)
                if (room > 0) room -= r.Reap(_cqes, room);
            for (int i = 0; i < _cqes.Count; i++) Handle(_cqes[i]);
            int returned = 0;
            foreach (Ring r in _rings)
            {
                returned += r.Returned;
                r.PublishBuffers();
            }
            // Arm as many receives that found no buffer as buffers came back, so a pass with no free buffer does not
            // arm receives that fail again at once.
            for (int budget = Math.Min(_rearm.Count, Math.Min(Math.Max(returned, 32), Ring.BufferCount / 2)); budget > 0; budget--)
            {
                var (ri, slot, gen) = _rearm.Dequeue();
                Ring r = _rings[ri];
                ref Conn c = ref r.Conns[slot];
                if (c.Gen == gen && (c.State & (Ring.Live | Ring.Receiving)) == Ring.Live) r.Recv(slot);
            }

            // 3. commit, then send. A pass between two windows leaves the transaction open for the next one, unless
            // it was idle or a checkpoint waits for the loop to let go of the WAL.
            bool committed = true;
            if (Db.InTxn && (window || Db.HoldWrites || _cqes.Count == 0)) committed = Db.Commit();
            if (window) _windowAt = now + WindowTicks;
            Flush(committed);
            ManageAccept();
            foreach (Ring r in _rings)
                if (r.Unsubmitted > 0) r.Submit();

            // 4. the sends' completions, while the arena still holds their bytes; the rest wait for the next pass
            _cqes.Clear();
            foreach (Ring r in _rings) r.Reap(_cqes, int.MaxValue);
            HandleSends();
            for (int i = 0; i < _rings.Count; i++)
            {
                // a send that did not finish inside the enter (not seen so far): wait for it
                while (_arenaSends[i] > 0)
                {
                    _rings[i].Enter((uint)_arenaSends[i], Ring.EnterGetEvents, null);
                    _cqes.Clear();
                    _rings[i].Reap(_cqes, int.MaxValue);
                    HandleSends();
                }
            }
            _out.Length = 0;
            _recordCount = 0;
            // the checkpoint thread runs only when this one sleeps
            uint giveWay = Db.GiveWay;
            if (giveWay != 0) Native.usleep(giveWay);
            open = 0;
            foreach (Ring r in _rings) open += r.Open;
        }
    }

    void HandleSends()
    {
        for (int i = 0; i < _cqes.Count; i++)
        {
            Cqe e = _cqes[i];
            ulong op = e.UserData & 7;
            if (op is Ring.UdSendArena or Ring.UdSendOwn) Handle(e);
            else if (op != Ring.UdIgnore) _carry.Add(e);
        }
    }

    void Handle(Cqe e)
    {
        Ring r = _rings[Ring.UdRing(e.UserData)];
        try
        {
            switch (e.UserData & 7)
            {
                case Ring.UdAccept: OnAccept(r, e); break;
                case Ring.UdRecv: OnRecv(r, e); break;
                case Ring.UdSendArena: OnSendArena(r, e); break;
                case Ring.UdSendOwn: OnSendOwn(r, e); break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"completion {e.UserData & 7} res {e.Res}: {ex.Message}");
        }
    }

    void OnAccept(Ring r, Cqe e)
    {
        if ((e.Flags & Ring.CqeFMore) == 0 && r.AcceptEnded(e.UserData))
        {
            // an error ends the multishot accept: arm it again a little later, not in a tight loop
            if (e.Res < 0 && e.Res != -Native.ECanceled) _acceptRetryAt = Environment.TickCount64 + 10;
        }
        if (e.Res < 0) return;
        int slot = e.Res;
        r.Conns[slot].State = Ring.Live;
        r.Open++;
        r.DelayAcks(slot);
        r.Recv(slot);
    }

    void OnRecv(Ring r, Cqe e)
    {
        int slot = Ring.UdSlot(e.UserData);
        ref Conn c = ref r.Conns[slot];
        if ((c.State & Ring.Live) != 0 && (c.Gen & 0xFFFFFF) == Ring.UdGen(e.UserData))
        {
            if ((e.Flags & Ring.CqeFMore) == 0) c.State &= unchecked((byte)~Ring.Receiving);
            if (e.Res > 0)
            {
                if ((c.State & Ring.Done) == 0) Process(r, slot, r.Buffer((int)(e.Flags >> 16)), e.Res);
                if ((c.State & (Ring.Live | Ring.Receiving)) == Ring.Live) r.Recv(slot);
            }
            else if (e.Res == -Native.ENoBufs)
            {
                if ((c.State & Ring.Receiving) == 0) _rearm.Enqueue((r.Index, slot, c.Gen));
            }
            else CloseSlot(r, slot); // 0: the peer closed; below 0: an error
        }
        if ((e.Flags & Ring.CqeFBuffer) != 0) r.Recycle((int)(e.Flags >> 16));
    }

    /// <summary>Answers the requests in <paramref name="n"/> new bytes (after what the connection kept), into the
    /// arena. The connection's responses of this pass stay together, in order, at the arena's end.</summary>
    void Process(Ring r, int slot, byte* src, int n)
    {
        ref Conn c = ref r.Conns[slot];
        ReadOnlySpan<byte> data = new(src, n);
        if (c.InLength > 0)
        {
            if (c.In!.Length - c.InLength < n)
            {
                byte[] bigger = ArrayPool<byte>.Shared.Rent(c.InLength + n);
                c.In.AsSpan(0, c.InLength).CopyTo(bigger);
                ArrayPool<byte>.Shared.Return(c.In);
                c.In = bigger;
            }
            data.CopyTo(c.In.AsSpan(c.InLength));
            c.InLength += n;
            data = new ReadOnlySpan<byte>(c.In, 0, c.InLength);
        }
        int ri = -1;
        if (c.RecordPass == _pass + 1)
        {
            ri = c.Record;
            ref Record rec = ref _records[ri];
            if (rec.Start + rec.Length != _out.Length)
            {
                _out.MoveToEnd(rec.Start, rec.Length);
                rec.Start = _out.Length - rec.Length;
            }
        }
        int before = _out.Length;
        int flags = (c.State & ContinueSent) != 0 ? Http.ContinueSent : 0;
        int consumed;
        try
        {
            consumed = Http.Serve(data, _out, ref flags);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"request: {ex.Message}");
            _out.Length = before;
            CloseSlot(r, slot);
            return;
        }
        c.State = (flags & Http.ContinueSent) != 0 ? (byte)(c.State | ContinueSent) : (byte)(c.State & ~ContinueSent);
        if ((flags & Http.Held) != 0 && (c.State & Ring.HeldWrite) == 0)
        {
            c.State |= Ring.HeldWrite;
            _held.Add((r.Index, slot, c.Gen));
        }
        bool close = (flags & Http.Close) != 0;
        int rest = data.Length - consumed;
        if (close || rest == 0)
        {
            ReturnInput(ref c);
        }
        else if (c.InLength > 0)
        {
            if (consumed > 0) Buffer.BlockCopy(c.In!, consumed, c.In!, 0, rest);
            c.InLength = rest;
        }
        else
        {
            c.In = ArrayPool<byte>.Shared.Rent(Math.Max(4096, rest));
            data[consumed..].CopyTo(c.In);
            c.InLength = rest;
        }
        int wrote = _out.Length - before;
        if (wrote == 0 && !close) return;
        if (ri < 0)
        {
            if (_recordCount == _records.Length) Array.Resize(ref _records, _records.Length * 2);
            ri = _recordCount++;
            _records[ri] = new Record { Ring = r.Index, Slot = slot, Gen = c.Gen, Start = before };
            c.RecordPass = _pass + 1;
            c.Record = ri;
        }
        _records[ri].Length += wrote;
        if (close)
        {
            _records[ri].Close = true;
            c.State |= Ring.Done; // later bytes are ignored
        }
    }

    static void ReturnInput(ref Conn c)
    {
        if (c.In is not null) ArrayPool<byte>.Shared.Return(c.In);
        c.In = null;
        c.InLength = 0;
    }

    /// <summary>Ends a connection: frees its buffers and queues SHUTDOWN + CLOSE. The generation changes, so the
    /// completions still in flight for it are recognized as stale. An in-flight send keeps its buffer alive until its
    /// completion comes back.</summary>
    void CloseSlot(Ring r, int slot)
    {
        ref Conn c = ref r.Conns[slot];
        if ((c.State & Ring.Live) == 0) return;
        ReturnInput(ref c);
        if (c.Pending is not null) ArrayPool<byte>.Shared.Return(c.Pending);
        c.Pending = null;
        c.PendingLength = 0;
        if ((c.State & Ring.Sending) != 0) _zombies[Ring.Ud(0, r.Index, slot, c.Gen, 0)] = c.Own!;
        c.Own = null;
        c.State = 0;
        c.RecordPass = 0;
        c.Gen++;
        r.Open--;
        r.ShutdownClose(slot);
    }

    static byte* AddressOf(byte[] a, int offset) =>
        (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(a)) + offset;

    /// <summary>Sends the connection's own buffer from its offset: a normal send, which waits for the socket.</summary>
    static void SendOwn(Ring r, int slot)
    {
        ref Conn c = ref r.Conns[slot];
        c.State |= Ring.Sending;
        r.Send(Ring.UdSendOwn, slot, AddressOf(c.Own!, c.OwnOffset), c.OwnLength, Ring.MsgNoSignal, 0);
    }

    /// <summary>The rest of a response the socket did not take at once: into the connection's own buffer.</summary>
    static void Spill(Ring r, int slot, ReadOnlySpan<byte> rest, bool close)
    {
        ref Conn c = ref r.Conns[slot];
        c.Own = GC.AllocateUninitializedArray<byte>(rest.Length, pinned: true);
        rest.CopyTo(c.Own);
        c.OwnOffset = 0;
        c.OwnLength = rest.Length;
        if (close) c.State |= Ring.CloseAfterSend;
        SendOwn(r, slot);
    }

    void OnSendArena(Ring r, Cqe e)
    {
        _arenaSends[r.Index]--;
        int slot = Ring.UdSlot(e.UserData);
        ref Conn c = ref r.Conns[slot];
        if ((c.State & Ring.Live) == 0 || (c.Gen & 0xFFFFFF) != Ring.UdGen(e.UserData)) return;
        Record rec = _records[Ring.UdExtra(e.UserData)];
        if (e.Res == rec.Length)
        {
            if (rec.Close) CloseSlot(r, slot);
        }
        else if (e.Res >= 0 || e.Res == -Native.EAgain || e.Res == -Native.EIntr)
        {
            int sent = Math.Max(e.Res, 0);
            Spill(r, slot, _out.Array.AsSpan(rec.Start + sent, rec.Length - sent), rec.Close);
        }
        else CloseSlot(r, slot);
    }

    void OnSendOwn(Ring r, Cqe e)
    {
        int slot = Ring.UdSlot(e.UserData);
        ref Conn c = ref r.Conns[slot];
        if ((c.State & Ring.Live) == 0 || (c.Gen & 0xFFFFFF) != Ring.UdGen(e.UserData))
        {
            // the connection closed meanwhile: the kernel is done with the buffer now
            _zombies.Remove(e.UserData & KeyMask);
            return;
        }
        c.State &= unchecked((byte)~Ring.Sending);
        if (e.Res == -Native.EAgain || e.Res == -Native.EIntr)
        {
            SendOwn(r, slot);
            return;
        }
        if (e.Res <= 0)
        {
            c.Own = null;
            CloseSlot(r, slot);
            return;
        }
        c.OwnOffset += e.Res;
        c.OwnLength -= e.Res;
        if (c.OwnLength > 0)
        {
            SendOwn(r, slot);
            return;
        }
        c.Own = null;
        if (c.PendingLength > 0)
        {
            byte[] p = c.Pending!;
            c.Own = GC.AllocateUninitializedArray<byte>(c.PendingLength, pinned: true);
            p.AsSpan(0, c.PendingLength).CopyTo(c.Own);
            ArrayPool<byte>.Shared.Return(p);
            c.OwnOffset = 0;
            c.OwnLength = c.PendingLength;
            c.Pending = null;
            c.PendingLength = 0;
            SendOwn(r, slot);
        }
        else if ((c.State & Ring.CloseAfterSend) != 0) CloseSlot(r, slot);
    }

    /// <summary>After the COMMIT: a send for each connection's responses. A connection whose own send is still in
    /// flight keeps them in its pending buffer until then. If the commit failed, the connections that got a
    /// response in this pass close: their writes are gone.</summary>
    void Flush(bool committed)
    {
        for (int i = 0; i < _recordCount; i++)
        {
            Record rec = _records[i];
            Ring r = _rings[rec.Ring];
            ref Conn c = ref r.Conns[rec.Slot];
            if ((c.State & Ring.Live) == 0 || c.Gen != rec.Gen) continue;
            if (!committed) CloseSlot(r, rec.Slot);
            else if ((c.State & Ring.Sending) != 0)
            {
                if (rec.Length > 0)
                {
                    if (c.Pending is null || c.Pending.Length - c.PendingLength < rec.Length)
                    {
                        byte[] bigger = ArrayPool<byte>.Shared.Rent(c.PendingLength + rec.Length);
                        if (c.Pending is not null)
                        {
                            c.Pending.AsSpan(0, c.PendingLength).CopyTo(bigger);
                            ArrayPool<byte>.Shared.Return(c.Pending);
                        }
                        c.Pending = bigger;
                    }
                    _out.Array.AsSpan(rec.Start, rec.Length).CopyTo(c.Pending.AsSpan(c.PendingLength));
                    c.PendingLength += rec.Length;
                }
                if (rec.Close) c.State |= Ring.CloseAfterSend;
            }
            else if (rec.Length > 0)
            {
                r.Send(Ring.UdSendArena, rec.Slot, _out.Address(rec.Start), rec.Length, Ring.MsgNoSignal | Ring.MsgDontWait, i);
                _arenaSends[rec.Ring]++;
            }
            else if (rec.Close) CloseSlot(r, rec.Slot);
        }
    }

    /// <summary>Answers, in this pass, the writes that waited for the window or for a checkpoint hold, oldest
    /// first.</summary>
    void ResumeHeld()
    {
        foreach (var (ri, slot, gen) in _held)
        {
            Ring r = _rings[ri];
            ref Conn c = ref r.Conns[slot];
            if (c.Gen != gen || (c.State & Ring.HeldWrite) == 0) continue;
            c.State &= unchecked((byte)~Ring.HeldWrite);
            if ((c.State & (Ring.Live | Ring.Done)) == Ring.Live) Process(r, slot, null, 0);
        }
        _held.Clear();
    }

    static int Reserve(Ring r) => Math.Min(4096, r.Slots / 4);

    /// <summary>Accepts on the first ring with room. A full table makes the kernel drop the socket it just
    /// accepted, and accept goes on between passes, so a ring stops accepting while it still has room.</summary>
    void ManageAccept()
    {
        Ring cur = _rings[_acceptRing];
        uint all = (1u << _listeners.Length) - 1;
        if (cur.Accepting != 0 && cur.Slots - cur.Open < Reserve(cur)) cur.StopAccept();
        if (cur.Accepting == all || Environment.TickCount64 < _acceptRetryAt) return;
        if (cur.Accepting != 0)
        {
            // some of its accepts ended with an error: arm those again
            cur.Accept(_listeners);
            return;
        }
        int target = -1;
        for (int i = 0; i < _rings.Count && target < 0; i++)
            if (_rings[i].Slots - _rings[i].Open >= 2 * Reserve(_rings[i])) target = i;
        if (target < 0 && _rings.Count < MaxRings)
        {
            try
            {
                target = AddRing().Index;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ring {_rings.Count}: {ex.Message}");
                _acceptRetryAt = Environment.TickCount64 + 1000;
            }
        }
        if (target < 0) return;
        _acceptRing = target;
        _rings[target].Accept(_listeners);
    }
}
