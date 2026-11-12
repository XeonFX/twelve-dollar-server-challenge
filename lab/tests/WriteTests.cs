using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Xunit;

/// <summary>Writes wait for the pass that commits, and the checkpoint round around them: the copy, the hold, the WAL
/// starting again, and a hold that takes too long.</summary>
public class WriteTests
{
    static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void A_write_waits_while_writes_are_closed_and_reads_go_on()
    {
        Harness.Open(Harness.Create(Harness.Users(3)));
        var o = new Arena(1 << 16);
        Api.Tick();
        byte[] like = Bytes("POST /posts/1/like HTTP/1.1\r\n\r\n");
        byte[] feed = Bytes("GET /feed HTTP/1.1\r\n\r\n");
        try
        {
            Db.WritesOpen = false;
            int flags = 0;
            Assert.Equal(0, Http.Serve(like, o, ref flags));
            Assert.Equal(Http.Held, flags & Http.Held);
            Assert.Equal(0, o.Length);
            // a read on another connection is answered meanwhile
            flags = 0;
            Assert.Equal(feed.Length, Http.Serve(feed, o, ref flags));
            Assert.Equal(0, flags & Http.Held);
            Assert.Contains("{\"posts\":[]}", Encoding.UTF8.GetString(o.Written));
            // a write behind a read on one connection: the read is answered, the write waits, in order
            o.Length = 0;
            flags = 0;
            byte[] both = [.. feed, .. like];
            Assert.Equal(feed.Length, Http.Serve(both, o, ref flags));
            Assert.Equal(Http.Held, flags & Http.Held);

            Db.WritesOpen = true;
            o.Length = 0;
            flags = 0;
            Assert.Equal(like.Length, Http.Serve(like, o, ref flags));
            Assert.StartsWith("HTTP/1.1 401 Unauthorized", Encoding.UTF8.GetString(o.Written)); // it ran: no token
        }
        finally
        {
            Db.WritesOpen = true;
            if (Db.InTxn) Db.Commit();
        }
    }

    static long NowMs => Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000);

    /// <summary>One pass of 20 posts of 450 bytes, as the loop would commit it, a millisecond after the last: a few
    /// thousand WAL frames a second, so a round's copy is short beside the time between rounds on any disk.</summary>
    static void WritePass(ref long next)
    {
        Thread.Sleep(1);
        var sql = new StringBuilder();
        for (int i = 0; i < 20; i++, next++)
            sql.Append($"INSERT INTO posts (id, user_id, body, created_at) VALUES ({next}, {next % 50 + 1}, '{new string('w', 450)}', '2026-01-01T00:00:00.000Z');");
        Db.Begin();
        Harness.Run(Db.Connection, sql.ToString());
        Assert.True(Db.Commit());
    }

    [Fact]
    public void Checkpoint_rounds_start_the_wal_again_and_lose_nothing()
    {
        string path = Harness.Create(Harness.Users(50));
        Harness.Open(path);
        Db.StartCheckpoints();
        string wal = path + "-wal";
        long next = 1, biggest = 0, sizeAtHold = 0, heldAt = 0;
        int holds = 0, restarts = 0, abandoned = 0, slow = 0;
        bool wasHeld = false, blockRestart = false;
        var watch = Stopwatch.StartNew();
        // Holds run as the loop runs them, except the third: there a transaction is left open, so the WAL cannot
        // start again and the hold must end by its time limit.
        while ((restarts < 3 || abandoned < 1) && watch.Elapsed.TotalSeconds < 90)
        {
            Db.CheckpointStep(NowMs);
            long size = File.Exists(wal) ? new FileInfo(wal).Length : 0;
            if (Db.HoldWrites)
            {
                if (!wasHeld)
                {
                    holds++;
                    heldAt = NowMs;
                    sizeAtHold = size;
                    blockRestart = holds == 3;
                    // a round starts at 4,096 frames of 4,120 bytes
                    Assert.True(size >= 4096L * 4120, $"hold {holds} began with a WAL of {size} bytes");
                }
                wasHeld = true;
                // reads go on during a hold
                Db.Begin();
                Harness.Clear();
                Assert.True(Reads.Feed());
                if (!blockRestart) Assert.True(Db.Commit());
                Thread.Sleep(1);
                continue;
            }
            if (wasHeld)
            {
                wasHeld = false;
                long waited = NowMs - heldAt;
                if (blockRestart)
                {
                    // the hold gave up at its limit, not before, and the WAL is as long as it was
                    Assert.True(waited >= 250, $"the blocked hold ended after {waited} ms");
                    Assert.True(size >= sizeAtHold, $"WAL {size} after a hold that could not restart it, {sizeAtHold} before");
                    abandoned++;
                    Assert.True(Db.Commit());
                    blockRestart = false;
                }
                else if (size == 4096 + 24 + 32)
                {
                    // the WAL started again: one frame, the checkpoint thread's own write
                    Assert.Equal(0, Db.WalFrames);
                    restarts++;
                }
                else
                {
                    // a disk too slow for the limit: the hold may only have ended by it
                    Assert.True(waited >= 250, $"a hold ended after {waited} ms with a WAL of {size} bytes");
                    slow++;
                }
            }
            biggest = Math.Max(biggest, size);
            WritePass(ref next); // never fails with SQLITE_BUSY: the checkpoint thread is off the write lock by now
        }
        Assert.True(restarts >= 3 && abandoned == 1, $"holds {holds}, restarts {restarts}, abandoned {abandoned}, slow {slow}, biggest WAL {biggest}");
        Assert.Equal(next - 1, Db.Scalar("SELECT count(*) FROM posts"));
        Assert.Equal(next - 1, Db.Scalar("SELECT max(id) FROM posts"));
        Assert.Equal(0, Db.Scalar("SELECT count(*) FROM pragma_integrity_check WHERE integrity_check <> 'ok'"));
        Harness.AssertSameAsSql([1, next - 1, next]);
    }
}
