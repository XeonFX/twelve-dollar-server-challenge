using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

/// <summary>
/// The request connection, used only by the loop thread, and the checkpoint thread with its own connection.
/// <para>Each pass of the loop runs in one transaction, reads and writes (group commit): SQLite takes its WAL locks
/// once per pass, and a response goes out only after the COMMIT that holds its write, in the WAL
/// (synchronous=NORMAL).</para>
/// <para>Both connections use SQLite's unix-excl VFS: the process locks the file once and keeps it, and the WAL
/// index is memory the two connections share (no -shm file, no file lock per transaction).</para>
/// <para>Checkpoints run on the checkpoint thread, at the lowest priority, so neither their copying nor their fsync
/// calls take the CPU from the loop. A round starts when the WAL passes <see cref="CheckpointFrames"/>: first a
/// PASSIVE checkpoint while writes go on, then the loop holds writes (a POST waits in its connection's input, reads
/// go on) while the thread copies the frames that came meanwhile and starts the WAL again with a write of its own.
/// The first frame of a new WAL fsyncs its header, and that wait belongs on the checkpoint thread too. A hold ends
/// after <see cref="HoldMs"/> at the latest; the WAL then grows on and the next round tries again. The loop gives
/// the thread a little of its time while writes wait for it (<see cref="GiveWay"/>), and whenever the WAL is far
/// past a round's size.</para>
/// </summary>
static unsafe class Db
{
    /// <summary>A round starts at this many WAL frames (16 MB): big enough that rounds are seconds apart, small
    /// enough that a round's fsync has little to write.</summary>
    const int CheckpointFrames = 4096;
    const int HoldMs = 250;
    /// <summary>Past this many WAL frames (128 MB) the loop makes room for the checkpoint thread in every pass: on
    /// a full CPU that thread would never run, and the WAL would grow until writes stall on the page cache.</summary>
    const int CrowdedFrames = 8 * CheckpointFrames;
    const int Idle = 0, Copying = 1, Finishing = 2, Abandoned = 3;

    static nint _db;
    static string _path = "";
    public static nint Feed, Post, InsertPost, InsertLike, PostExists, Ping;
    static nint _begin, _commit, _rollback;

    /// <summary>True while the pass transaction is open.</summary>
    public static bool InTxn;
    /// <summary>The loop holds new writes until the checkpoint thread started the WAL again.</summary>
    public static bool HoldWrites;
    /// <summary>Set by the loop for each pass: writes run only in a pass that commits.</summary>
    public static bool WritesOpen = true;
    /// <summary>WAL frames after the loop's last commit (sqlite3_wal_hook).</summary>
    static int _walFrames;

    // the checkpoint round: _phase belongs to the loop; the rest is shared with the checkpoint thread
    static int _phase;
    static long _holdSince;
    /// <summary>What the checkpoint thread should do next: 0 nothing, 1 copy, 2 copy the rest and start the WAL again.</summary>
    static int _request;
    /// <summary>Counts the requests the checkpoint thread finished.</summary>
    static int _finished;
    static int _seenFinished;
    /// <summary>1 while the checkpoint thread starts the WAL again, 2 while the loop ends a hold that timed out: the
    /// loop never lets writes go while that thread holds the write lock, and that thread never takes it after a
    /// hold ended.</summary>
    static int _gate;
    static readonly AutoResetEvent Wake = new(false);

    public static void Open(string path, int cacheKb)
    {
        Check(Sqlite.initialize()); // required: SQLite is built with SQLITE_OMIT_AUTOINIT
        _path = path;
        _db = OpenConnection(path);
        Exec(_db, "PRAGMA journal_mode = WAL");
        Exec(_db, "PRAGMA synchronous = NORMAL");
        Exec(_db, "PRAGMA wal_autocheckpoint = 0"); // the checkpoint thread does it
        // No mmap: a page in SQLite's own cache is found with one hash lookup and keeps its parsed b-tree header.
        Exec(_db, "PRAGMA mmap_size = 0");
        Exec(_db, $"PRAGMA cache_size = -{cacheKb}");
        // statement journals in memory: a group-commit transaction keeps one for each write statement
        Exec(_db, "PRAGMA temp_store = MEMORY");
        // after the pragma: it clears the hook
        Sqlite.wal_hook(_db, &OnCommit, 0);

        const string postSelect =
            "SELECT p.id, p.body, p.created_at, u.username, (SELECT count(*) FROM likes l WHERE l.post_id = p.id) " +
            "FROM posts p JOIN users u ON u.id = p.user_id";
        Feed = Prepare(postSelect + " ORDER BY p.created_at DESC, p.id DESC LIMIT 20");
        Post = Prepare(postSelect + " WHERE p.id = ?1");
        // created_at is bound, in the format of the column's default, and the id comes from
        // sqlite3_last_insert_rowid: RETURNING would buffer its row in a temporary table for each insert
        InsertPost = Prepare("INSERT INTO posts (user_id, body, created_at) VALUES (?1, ?2, ?3)");
        // Inserts only when the post exists: no change means "already liked" or "no such post".
        InsertLike = Prepare(
            "INSERT INTO likes (user_id, post_id, created_at) SELECT ?1, ?2, ?3 " +
            "WHERE EXISTS (SELECT 1 FROM posts WHERE id = ?2) ON CONFLICT (user_id, post_id) DO NOTHING");
        PostExists = Prepare("SELECT 1 FROM posts WHERE id = ?1");
        Ping = Prepare("SELECT 1");
        _begin = Prepare("BEGIN");
        _commit = Prepare("COMMIT");
        _rollback = Prepare("ROLLBACK");
    }

    /// <summary>Reads into SQLite's page cache, while memory is plentiful, the pages that writes and reads later
    /// touch at random: the likes key index (each like lands on a random leaf), the posts-by-user index and the
    /// users table (each post's author). Later the box's memory belongs to sockets, and a page read from disk
    /// then would stop the loop.</summary>
    public static void Warm()
    {
        Exec(_db, "SELECT sum(user_id) FROM likes INDEXED BY sqlite_autoindex_likes_1");
        Exec(_db, "SELECT sum(user_id) FROM posts INDEXED BY posts_user_id_idx");
        Exec(_db, "SELECT sum(length(username)) FROM users");
    }

    /// <summary>Turns the B-tree reads on if the schema is the expected one and they answer what SQL answers.</summary>
    public static void StartReads()
    {
        if (!Reads.Init(_db))
        {
            Console.Error.WriteLine("B-tree reads off (unexpected schema): SQL for every read");
            return;
        }
        Reads.On = true;
        string? difference = Api.SelfTest();
        if (difference is null) return;
        Reads.On = false;
        Console.Error.WriteLine($"B-tree reads off ({difference}): SQL for every read");
    }

    static nint OpenConnection(string path)
    {
        nint db;
        fixed (byte* p = Utf8z(path))
        fixed (byte* vfs = "unix-excl\0"u8)
        {
            int rc = Sqlite.open_v2(p, &db, Sqlite.OpenReadWrite | Sqlite.OpenNoMutex, vfs);
            if (rc != Sqlite.Ok) throw new SqliteException($"cannot open {path}: {ErrorOf(db)}");
        }
        return db;
    }

    static byte[] Utf8z(string s)
    {
        var b = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, b);
        return b;
    }

    static void Exec(nint db, string sql)
    {
        fixed (byte* p = Utf8z(sql))
            if (Sqlite.exec(db, p, 0, 0, 0) != Sqlite.Ok) throw new SqliteException($"{sql}: {ErrorOf(db)}");
    }

    static nint Prepare(string sql, uint flags = Sqlite.PreparePersistent)
    {
        nint stmt;
        fixed (byte* p = Utf8z(sql))
            if (Sqlite.prepare_v3(_db, p, -1, flags, &stmt, 0) != Sqlite.Ok)
                throw new SqliteException($"{sql}: {ErrorOf(_db)}");
        return stmt;
    }

    /// <summary>The first column of the first row as an integer; 0 when there is no row.</summary>
    public static long Scalar(string sql)
    {
        nint st = Prepare(sql, 0);
        long v = Sqlite.step_slow(st) == Sqlite.Row ? Sqlite.column_int64(st, 0) : 0;
        Sqlite.finalize(st);
        return v;
    }

    /// <summary>The first column of every row, as integers.</summary>
    public static long[] Column(string sql)
    {
        var rows = new System.Collections.Generic.List<long>();
        nint st = Prepare(sql, 0);
        while (Sqlite.step_slow(st) == Sqlite.Row) rows.Add(Sqlite.column_int64(st, 0));
        Sqlite.finalize(st);
        return rows.ToArray();
    }

    static string ErrorOf(nint db) => db == 0 ? "out of memory" : Marshal.PtrToStringUTF8((nint)Sqlite.errmsg(db)) ?? "sqlite error";

    public static string Error => ErrorOf(_db);

    /// <summary>The request connection and the WAL size the loop last saw, for tests.</summary>
    internal static nint Connection => _db;
    internal static int WalFrames => _walFrames;

    static void Check(int rc)
    {
        if (rc != Sqlite.Ok) throw new SqliteException($"sqlite error {rc}");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnCommit(nint arg, nint db, byte* name, int frames)
    {
        _walFrames = frames;
        return 0;
    }

    /// <summary>Opens the pass transaction if none is open. Call before every statement.</summary>
    public static void Begin()
    {
        if (InTxn) return;
        int rc = Sqlite.step(_begin);
        Sqlite.reset(_begin);
        if (rc != Sqlite.Done) throw new SqliteException(Error);
        InTxn = true;
    }

    /// <summary>Commits the pass transaction; false when the commit failed and it was rolled back.</summary>
    public static bool Commit()
    {
        InTxn = false;
        Reads.EndPass();
        // with the GC transition: the WAL hook calls back into managed code
        bool ok = Sqlite.step_slow(_commit) == Sqlite.Done;
        Sqlite.reset(_commit);
        if (!ok)
        {
            Sqlite.step(_rollback);
            Sqlite.reset(_rollback);
        }
        return ok;
    }

    /// <summary>Steps once: true on a row, false when done, and throws on an error.</summary>
    public static bool Row(nint stmt)
    {
        int rc = Sqlite.step(stmt);
        if (rc == Sqlite.Row) return true;
        if (rc == Sqlite.Done) return false;
        throw new SqliteException(Error);
    }

    /// <summary>Rows the last INSERT changed.</summary>
    public static int Changes => Sqlite.changes(_db);

    /// <summary>The rowid of the last INSERT.</summary>
    public static long LastId => Sqlite.last_insert_rowid(_db);

    // ---- checkpoints ----

    /// <summary>Moves the checkpoint round on. The loop calls it once per pass, with the time in milliseconds; true
    /// when a hold just ended. While <see cref="HoldWrites"/> is set the loop ends its transaction with every
    /// pass: the WAL starts again only when no pass reads it.</summary>
    public static bool CheckpointStep(long now)
    {
        switch (_phase)
        {
            case Idle:
                if (_walFrames < CheckpointFrames) return false;
                Ask(1);
                _phase = Copying;
                return false;
            case Copying:
                if (!Finished()) return false;
                // the thread copied what there was; hold writes while it copies the rest
                HoldWrites = true;
                _holdSince = now;
                Ask(2);
                _phase = Finishing;
                return false;
            case Finishing:
                if (Finished())
                {
                    _phase = Idle;
                    _walFrames = 0; // the next commit says how many there really are
                    Volatile.Write(ref HoldWrites, false);
                    return true;
                }
                if (now - _holdSince < HoldMs || Interlocked.CompareExchange(ref _gate, 2, 0) != 0) return false;
                // too slow: writes go on, and the thread, once it notices, ends its round without the restart
                Volatile.Write(ref HoldWrites, false);
                Volatile.Write(ref _gate, 0);
                _phase = Abandoned;
                return true;
            default:
                if (Finished()) _phase = Idle;
                return false;
        }
    }

    /// <summary>How long the loop should sleep after this pass so that the checkpoint thread runs, in microseconds:
    /// a little while writes are held for it, more when the WAL is far too long, else nothing.</summary>
    public static uint GiveWay => HoldWrites ? 200u : _phase != Idle && _walFrames >= CrowdedFrames ? 1000u : 0u;

    static void Ask(int what)
    {
        Volatile.Write(ref _request, what);
        Wake.Set();
    }

    static bool Finished()
    {
        int n = Volatile.Read(ref _finished);
        if (n == _seenFinished) return false;
        _seenFinished = n;
        return true;
    }

    /// <summary>Starts the checkpoint thread (after <see cref="Open"/>).</summary>
    public static void StartCheckpoints()
    {
        nint cdb = OpenConnection(_path);
        // Read the database once, so this connection is in WAL mode; until then a checkpoint here does nothing.
        Exec(cdb, "PRAGMA journal_mode = WAL");
        Exec(cdb, "PRAGMA synchronous = NORMAL");
        Exec(cdb, "PRAGMA wal_autocheckpoint = 0");
        // The WAL is cut to nothing when it starts again: its frames then go to new file pages, never over old
        // ones that the kernel may have dropped and would have to read back first.
        Exec(cdb, "PRAGMA journal_size_limit = 0");
        nint st;
        fixed (byte* p = "PRAGMA user_version\0"u8)
            if (Sqlite.prepare_v3(cdb, p, -1, 0, &st, 0) != Sqlite.Ok) throw new SqliteException(ErrorOf(cdb));
        long userVersion = Sqlite.step_slow(st) == Sqlite.Row ? Sqlite.column_int64(st, 0) : 0;
        Sqlite.finalize(st);
        // The same user_version again: a write that changes nothing, but it starts the WAL again on this thread.
        byte[] restart = Utf8z($"BEGIN IMMEDIATE; PRAGMA user_version = {userVersion}; COMMIT");
        byte[] rollback = Utf8z("ROLLBACK");
        // A descriptor of the database file for posix_fadvise. Never closed: closing any descriptor of the file
        // would drop the process's lock on it.
        int fd = -1;
        if (OperatingSystem.IsLinux())
            fixed (byte* p = Utf8z(_path)) fd = Native.open(p, Native.OReadOnly | Native.OCloExec);
        var thread = new Thread(() => CheckpointLoop(cdb, restart, rollback, fd), 256 * 1024)
        {
            IsBackground = true,
            Name = "checkpoint",
        };
        thread.Start();
    }

    static void CheckpointLoop(nint cdb, byte[] restart, byte[] rollback, int fd)
    {
        if (OperatingSystem.IsLinux()) Native.LowPriority();
        while (true)
        {
            Wake.WaitOne();
            int what = Interlocked.Exchange(ref _request, 0);
            if (what == 0) continue;
            int logFrames, copied;
            // PASSIVE: copies the WAL into the database and fsyncs both, without blocking reads or writes
            Sqlite.wal_checkpoint_v2(cdb, null, Sqlite.CheckpointPassive, &logFrames, &copied);
            while (what == 2 && Volatile.Read(ref HoldWrites))
            {
                if (copied == logFrames && Interlocked.CompareExchange(ref _gate, 1, 0) == 0)
                {
                    bool restarted = false;
                    if (Volatile.Read(ref HoldWrites))
                    {
                        // RESTART has nothing left to copy: it only finds out whether a pass still reads the WAL.
                        // If none does, the write after it starts the WAL again, with the header's fsync.
                        if (Sqlite.wal_checkpoint_v2(cdb, null, Sqlite.CheckpointRestart, null, null) == Sqlite.Ok)
                        {
                            fixed (byte* p = restart)
                            {
                                restarted = Sqlite.exec(cdb, p, 0, 0, 0) == Sqlite.Ok;
                                if (!restarted)
                                    fixed (byte* r = rollback)
                                        Sqlite.exec(cdb, r, 0, 0, 0);
                            }
                        }
                    }
                    Volatile.Write(ref _gate, 0);
                    if (restarted) break;
                }
                // a pass of the loop was reading the WAL, or frames are left: again in a moment
                Thread.Sleep(1);
                Sqlite.wal_checkpoint_v2(cdb, null, Sqlite.CheckpointPassive, &logFrames, &copied);
            }
            // The database pages just written are synced, and the server reads from SQLite's own page cache: the
            // kernel's copies would only have to be reclaimed, on the one CPU, when memory gets short.
            if (fd >= 0) Native.posix_fadvise(fd, 0, 0, Native.FadvDontNeed);
            Interlocked.Increment(ref _finished);
        }
    }
}
