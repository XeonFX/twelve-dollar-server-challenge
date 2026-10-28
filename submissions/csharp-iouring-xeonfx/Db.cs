using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

/// <summary>
/// The request connection, used only by the loop thread, and the checkpoint thread with its own connection.
/// <para>Each pass of the loop runs in one transaction, reads and writes (group commit): SQLite takes its WAL locks
/// once per pass, and a response goes out only after the COMMIT that holds its write, durable in the WAL
/// (synchronous=NORMAL).</para>
/// <para>The checkpoint thread copies the WAL into the database file with PASSIVE checkpoints, so their fsync calls
/// never stop the loop. A PASSIVE checkpoint never blocks writes, so under steady writes it never finds the whole WAL
/// copied and the WAL would grow without end. So at <see cref="HoldFrames"/> the loop holds writes (a POST waits in
/// its connection's input, reads go on) until the checkpoint thread has copied every frame and started the WAL
/// again itself, with a write of its own: the first frame of a new WAL fsyncs it, and that wait belongs on the
/// checkpoint thread.</para>
/// </summary>
static unsafe class Db
{
    static nint _db;
    static string _path = "";
    public static nint FeedIds, FeedRange, LikeRange, Feed, Post, InsertPost, InsertLike, PostExists, Ping;
    static nint _begin, _commit, _rollback;

    /// <summary>True while the pass transaction is open.</summary>
    public static bool InTxn;

    /// <summary>Commits since the start; the checkpoint thread checkpoints when it moved.</summary>
    static int _commits;

    /// <summary>WAL size in frames after the last checkpoint (written by the checkpoint thread).</summary>
    public static int WalFrames;
    /// <summary>Set by the loop: new writes wait until the checkpoint thread has copied every frame.</summary>
    public static bool HoldWrites;
    /// <summary>Set by the checkpoint thread when, with writes held, every frame is copied and the WAL started again.</summary>
    public static bool CaughtUp;
    /// <summary>1 while the checkpoint thread starts the WAL again, 2 while the loop ends a hold that timed out: the
    /// loop never lets writes go while that thread holds the write lock, and that thread never starts the WAL after a
    /// hold ended.</summary>
    public static int RestartGate;

    /// <summary>15,000 frames of 4 KB (under the 64 MB journal_size_limit). A hold lasts as long as the checkpoint's
    /// fsync calls, not its copying, so a big WAL means fewer holds, not longer ones (measured by the F# entry).</summary>
    public const int HoldFrames = 15000;

    public static void Open(string path)
    {
        Check(Sqlite.initialize()); // required: SQLite is built with SQLITE_OMIT_AUTOINIT
        _path = path;
        _db = OpenConnection(path);
        Exec(_db, "PRAGMA journal_mode = WAL");
        Exec(_db, "PRAGMA wal_autocheckpoint = 0"); // the checkpoint thread does it
        Exec(_db, "PRAGMA journal_size_limit = 67108864"); // a WAL that grew past 64 MB shrinks when it starts again
        Exec(_db, "PRAGMA synchronous = NORMAL");
        Exec(_db, "PRAGMA busy_timeout = 5000");
        // No mmap: a page in SQLite's own cache is found with one hash lookup and keeps its parsed b-tree header.
        Exec(_db, "PRAGMA mmap_size = 0");
        Exec(_db, "PRAGMA cache_size = -32768");
        // statement journals in memory: a group-commit transaction keeps one for each write statement
        Exec(_db, "PRAGMA temp_store = MEMORY");

        const string postSelect =
            "SELECT p.id, p.body, p.created_at, u.username, (SELECT count(*) FROM likes l WHERE l.post_id = p.id) " +
            "FROM posts p JOIN users u ON u.id = p.user_id";
        Feed = Prepare(postSelect + " ORDER BY p.created_at DESC, p.id DESC LIMIT 20");
        Post = Prepare(postSelect + " WHERE p.id = ?1");
        FeedIds = Prepare("SELECT id FROM posts ORDER BY created_at DESC, id DESC LIMIT 20");
        FeedRange = Prepare(
            "SELECT p.id, p.body, p.created_at, u.username FROM posts p JOIN users u ON u.id = p.user_id " +
            "WHERE p.id BETWEEN ?1 AND ?2 ORDER BY p.id DESC");
        LikeRange = Prepare("SELECT post_id, count(*) FROM likes WHERE post_id BETWEEN ?1 AND ?2 GROUP BY post_id");
        InsertPost = Prepare("INSERT INTO posts (user_id, body) VALUES (?1, ?2) RETURNING id, created_at");
        // Inserts only when the post exists: no change means "already liked" or "no such post".
        InsertLike = Prepare(
            "INSERT INTO likes (user_id, post_id) SELECT ?1, ?2 WHERE EXISTS (SELECT 1 FROM posts WHERE id = ?2) " +
            "ON CONFLICT (user_id, post_id) DO NOTHING");
        PostExists = Prepare("SELECT 1 FROM posts WHERE id = ?1");
        Ping = Prepare("SELECT 1");
        _begin = Prepare("BEGIN");
        _commit = Prepare("COMMIT");
        _rollback = Prepare("ROLLBACK");
    }

    static nint OpenConnection(string path)
    {
        nint db;
        fixed (byte* p = Utf8z(path))
        {
            int rc = Sqlite.open_v2(p, &db, Sqlite.OpenReadWrite, null);
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

    static nint Prepare(string sql)
    {
        nint stmt;
        fixed (byte* p = Utf8z(sql))
            if (Sqlite.prepare_v3(_db, p, -1, Sqlite.PreparePersistent, &stmt, 0) != Sqlite.Ok)
                throw new SqliteException($"{sql}: {ErrorOf(_db)}");
        return stmt;
    }

    static string ErrorOf(nint db) => db == 0 ? "out of memory" : Marshal.PtrToStringUTF8((nint)Sqlite.errmsg(db)) ?? "sqlite error";

    public static string Error => ErrorOf(_db);

    static void Check(int rc)
    {
        if (rc != Sqlite.Ok) throw new SqliteException($"sqlite error {rc}");
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
        bool ok = Sqlite.step(_commit) == Sqlite.Done;
        Sqlite.reset(_commit);
        if (ok) Volatile.Write(ref _commits, _commits + 1);
        else
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

    /// <summary>Starts the checkpoint thread (after <see cref="Open"/>).</summary>
    public static void StartCheckpoints()
    {
        nint cdb = OpenConnection(_path);
        // Read the database once, so this connection is in WAL mode; until then a checkpoint here does nothing.
        Exec(cdb, "PRAGMA journal_mode = WAL");
        Exec(cdb, "PRAGMA busy_timeout = 5000");
        long userVersion;
        nint st;
        fixed (byte* p = "PRAGMA user_version\0"u8)
            if (Sqlite.prepare_v3(cdb, p, -1, 0, &st, 0) != Sqlite.Ok) throw new SqliteException(ErrorOf(cdb));
        userVersion = Sqlite.step_slow(st) == Sqlite.Row ? Sqlite.column_int64(st, 0) : 0;
        Sqlite.finalize(st);
        // The same user_version again: a write that changes nothing, but it starts the WAL again on this thread.
        byte[] restart = Utf8z($"BEGIN IMMEDIATE; PRAGMA user_version = {userVersion}; COMMIT");
        byte[] rollback = Utf8z("ROLLBACK");
        var thread = new Thread(() => CheckpointLoop(cdb, restart, rollback), 256 * 1024)
        {
            IsBackground = true,
            Name = "checkpoint",
        };
        thread.Start();
    }

    static void CheckpointLoop(nint cdb, byte[] restart, byte[] rollback)
    {
        int seen = -1;
        while (true)
        {
            // while writes are held, go on at once: the loop waits for this checkpoint
            bool held = Volatile.Read(ref HoldWrites);
            Thread.Sleep(held ? 1 : 50);
            held = Volatile.Read(ref HoldWrites);
            int now = Volatile.Read(ref _commits);
            // Caught up and the hold not ended yet: nothing to do. A checkpoint now would copy the frame that started
            // the WAL again, and the loop's next write would start it once more, with the fsync.
            if (held && Volatile.Read(ref CaughtUp)) continue;
            if (now == seen && !held) continue;
            seen = now;
            int logFrames, copied;
            // PASSIVE: copies the WAL into the database and fsyncs both, without blocking reads or writes
            Sqlite.wal_checkpoint_v2(cdb, null, 0, &logFrames, &copied);
            Volatile.Write(ref WalFrames, logFrames);
            if (!held || copied < logFrames) continue;
            if (logFrames <= 0)
            {
                Volatile.Write(ref CaughtUp, true);
                continue;
            }
            if (Interlocked.CompareExchange(ref RestartGate, 1, 0) != 0) continue;
            if (Volatile.Read(ref HoldWrites))
            {
                // If a reader still used the old frames, SQLite appends instead: the WAL grows until the next hold.
                fixed (byte* p = restart)
                {
                    if (Sqlite.exec(cdb, p, 0, 0, 0) == Sqlite.Ok) Volatile.Write(ref CaughtUp, true);
                    else
                        fixed (byte* r = rollback)
                            Sqlite.exec(cdb, r, 0, 0, 0);
                }
            }
            Volatile.Write(ref RestartGate, 0);
        }
    }
}
