using System;
using System.Runtime.InteropServices;

/// <summary>The SQLite C API with raw handles. The calls the loop thread makes per request (step, reset, bind,
/// column) skip the GC transition: SQLite never calls back into .NET, and in WAL mode with synchronous=NORMAL a
/// commit only writes, it does not wait for the disk.</summary>
static unsafe partial class Sqlite
{
    public const int Ok = 0, Row = 100, Done = 101;
    public const int OpenReadWrite = 0x02, OpenNoMutex = 0x8000;
    /// <summary>SQLITE_STATIC: the bound bytes stay valid until the statement is reset.</summary>
    public const nint Static = 0;
    /// <summary>SQLITE_PREPARE_PERSISTENT: the statement lives as long as the process.</summary>
    public const uint PreparePersistent = 1;
    /// <summary>SQLITE_TRANSIENT: SQLite copies a bound value.</summary>
    public static readonly nint Transient = -1;

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_initialize")]
    public static partial int initialize();

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_open_v2")]
    public static partial int open_v2(byte* filename, nint* db, int flags, byte* vfs);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_exec")]
    public static partial int exec(nint db, byte* sql, nint callback, nint arg, nint errmsg);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_prepare_v3")]
    public static partial int prepare_v3(nint db, byte* sql, int length, uint flags, nint* stmt, nint tail);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_finalize")]
    public static partial int finalize(nint stmt);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_errmsg")]
    public static partial byte* errmsg(nint db);

    public const int CheckpointPassive = 0, CheckpointRestart = 2;

    /// <summary>sqlite3_wal_checkpoint_v2, on the checkpoint thread.</summary>
    [LibraryImport("sqlite3", EntryPoint = "sqlite3_wal_checkpoint_v2")]
    public static partial int wal_checkpoint_v2(nint db, byte* name, int mode, int* logFrames, int* checkpointed);

    /// <summary>step for the checkpoint thread, with the GC transition: its statements wait for the disk.</summary>
    [LibraryImport("sqlite3", EntryPoint = "sqlite3_step")]
    public static partial int step_slow(nint stmt);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_step"), SuppressGCTransition]
    public static partial int step(nint stmt);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_reset"), SuppressGCTransition]
    public static partial int reset(nint stmt);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_bind_int64"), SuppressGCTransition]
    public static partial int bind_int64(nint stmt, int index, long value);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_bind_text"), SuppressGCTransition]
    public static partial int bind_text(nint stmt, int index, byte* text, int length, nint destructor);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_column_int64"), SuppressGCTransition]
    public static partial long column_int64(nint stmt, int column);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_column_text"), SuppressGCTransition]
    public static partial byte* column_text(nint stmt, int column);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_column_bytes"), SuppressGCTransition]
    public static partial int column_bytes(nint stmt, int column);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_changes"), SuppressGCTransition]
    public static partial int changes(nint db);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_last_insert_rowid"), SuppressGCTransition]
    public static partial long last_insert_rowid(nint db);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3_wal_hook")]
    public static partial nint wal_hook(nint db, delegate* unmanaged[Cdecl]<nint, nint, byte*, int, int> hook, nint arg);

    // ---- SQLite's B-tree layer. These are internal functions of the amalgamation (SQLITE_PRIVATE), exported by
    // building it with -DSQLITE_PRIVATE= (its own switch; the source is not changed). The reads of /feed and
    // /posts/:id call them as SQLite's bytecode engine would. ----

    /// <summary>BTREE_SCHEMA_VERSION: the schema cookie's slot in <see cref="BtreeGetMeta"/>.</summary>
    public const int MetaSchemaVersion = 1;
    /// <summary>MEM_Int, and the sizes and field offsets of Mem and UnpackedRecord in 3.54.1 on a 64-bit build
    /// without SQLITE_DEBUG (checked against the source; the startup self-test would catch a difference).</summary>
    public const ushort MemInt = 4;
    public const int MemSize = 56, MemFlags = 20, KeySize = 40, KeyMem = 8, KeyFields = 28, KeyDefaultRc = 30;

    [LibraryImport("sqlite3", EntryPoint = "sqlite3DbNameToBtree")]
    public static partial nint DbNameToBtree(nint db, byte* name);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3KeyInfoAlloc")]
    public static partial nint KeyInfoAlloc(nint db, int fields, int extra);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3BtreeCursorSize")]
    public static partial int BtreeCursorSize();

    [LibraryImport("sqlite3", EntryPoint = "sqlite3BtreeBeginTrans"), SuppressGCTransition]
    public static partial int BtreeBeginTrans(nint bt, int write, int* schemaVersion);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3BtreeGetMeta"), SuppressGCTransition]
    public static partial void BtreeGetMeta(nint bt, int index, uint* value);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3BtreeCursorZero"), SuppressGCTransition]
    public static partial void BtreeCursorZero(byte* cursor);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3BtreeCursor"), SuppressGCTransition]
    public static partial int BtreeCursor(nint bt, uint root, int write, nint keyInfo, byte* cursor);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3BtreeCloseCursor"), SuppressGCTransition]
    public static partial int BtreeCloseCursor(byte* cursor);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3BtreeFirst"), SuppressGCTransition]
    public static partial int BtreeFirst(byte* cursor, int* empty);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3BtreeNext"), SuppressGCTransition]
    public static partial int BtreeNext(byte* cursor, int flags);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3BtreeEof"), SuppressGCTransition]
    public static partial int BtreeEof(byte* cursor);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3BtreeTableMoveto"), SuppressGCTransition]
    public static partial int BtreeTableMoveto(byte* cursor, long key, int bias, int* result);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3BtreeIndexMoveto"), SuppressGCTransition]
    public static partial int BtreeIndexMoveto(byte* cursor, byte* key, int* result);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3BtreePayloadSize"), SuppressGCTransition]
    public static partial uint BtreePayloadSize(byte* cursor);

    [LibraryImport("sqlite3", EntryPoint = "sqlite3BtreePayloadFetch"), SuppressGCTransition]
    public static partial byte* BtreePayloadFetch(byte* cursor, uint* available);

    /// <summary>A TEXT column as UTF-8, valid until the statement steps or resets (text before bytes, as the docs
    /// ask).</summary>
    public static ReadOnlySpan<byte> Text(nint stmt, int column)
    {
        byte* p = column_text(stmt, column);
        return new ReadOnlySpan<byte>(p, column_bytes(stmt, column));
    }
}

sealed class SqliteException(string message) : Exception(message);
