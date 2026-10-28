using System;
using System.Runtime.InteropServices;

/// <summary>The SQLite C API with raw handles. The calls the loop thread makes per request (step, reset, bind,
/// column) skip the GC transition: SQLite never calls back into .NET, and in WAL mode with synchronous=NORMAL a
/// commit only writes, it does not wait for the disk.</summary>
static unsafe partial class Sqlite
{
    public const int Ok = 0, Row = 100, Done = 101;
    public const int OpenReadWrite = 0x02;
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

    /// <summary>sqlite3_wal_checkpoint_v2; mode 0 is SQLITE_CHECKPOINT_PASSIVE.</summary>
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

    /// <summary>A TEXT column as UTF-8, valid until the statement steps or resets (text before bytes, as the docs
    /// ask).</summary>
    public static ReadOnlySpan<byte> Text(nint stmt, int column)
    {
        byte* p = column_text(stmt, column);
        return new ReadOnlySpan<byte>(p, column_bytes(stmt, column));
    }
}

sealed class SqliteException(string message) : Exception(message);
