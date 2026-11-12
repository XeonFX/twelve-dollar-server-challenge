using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// GET /feed and GET /posts/:id on SQLite's B-tree layer: its own cursor functions (sqlite3BtreeFirst, Next,
/// TableMoveto, IndexMoveto, PayloadFetch) find the rows, and this class decodes the record bytes they point at
/// (https://sqlite.org/fileformat2.html#record_format). No bytecode engine and no sqlite3_column_* calls. The pages
/// still come through SQLite's pager, WAL and page cache, inside the pass's transaction, so a request reads its
/// rows while it is served.
/// <para>Kept between requests: the four root pages and the schema cookie (read at startup, the cookie checked
/// again in every pass), two KeyInfo objects and the cursors' memory. Cursors stay open until the pass commits; a
/// write in between makes SQLite save and reseek them, as it does for any statement's cursor.</para>
/// <para>A row the decoder does not cover (an overflow page, a type the schema does not promise, a post without
/// its user) is left to the SQL statements, and so is every read if the startup self-test finds a difference.</para>
/// <para>The technique is rust-iouring-kensac's and fsharp-uring-delneg's (the maintainer ruled it in on
/// 2026-10-31); the per-post cursors that later requests of a pass find already in place are the F# entry's.</para>
/// </summary>
static unsafe class Reads
{
    /// <summary>False until <see cref="Init"/> and the self-test pass: every read then uses SQL.</summary>
    public static bool On;

    public const int Found = 1, Missing = 0, UseSql = -1;

    // Cursor slots. A feed reads its n-th post and that post's author through cursors of their own: the requests of
    // one pass mostly ask for the same rows, and TableMoveto returns at once from a cursor already on the key.
    const int Top = 0, Likes = 1, Post = 2, User = 3, FeedPost = 4, FeedUser = 24, CursorCount = 44;
    const int FeedSize = 20;
    /// <summary>Feed ids this close together get their like counts from one walk of likes_post_id_idx.</summary>
    const int Window = 512;
    const uint Bad = uint.MaxValue;

    static nint _bt, _topKey, _likesKey;
    static uint _cookie, _rootPosts, _rootUsers, _rootTop, _rootLikes;
    static byte* _cursors, _search;
    static int _cursorSize;
    static ulong _open;
    static bool _reading;
    static long* _ids, _counts, _tally;

    struct Row
    {
        public byte* Body, Created, Author;
        public int BodyLength, CreatedLength, AuthorLength;
        public long UserId;
    }

    /// <summary>Looks the schema up and allocates the cursors. False when the schema is not the one the decoders
    /// assume (they read columns by position).</summary>
    public static bool Init(nint db)
    {
        _rootPosts = (uint)Db.Scalar("SELECT rootpage FROM sqlite_schema WHERE name = 'posts'");
        _rootUsers = (uint)Db.Scalar("SELECT rootpage FROM sqlite_schema WHERE name = 'users'");
        _rootTop = (uint)Db.Scalar("SELECT rootpage FROM sqlite_schema WHERE name = 'posts_created_at_id_idx'");
        _rootLikes = (uint)Db.Scalar("SELECT rootpage FROM sqlite_schema WHERE name = 'likes_post_id_idx'");
        if (_rootPosts == 0 || _rootUsers == 0 || _rootTop == 0 || _rootLikes == 0) return false;
        bool schema =
            Db.Scalar("SELECT count(*) FROM pragma_table_info('posts') WHERE (cid=0 AND name='id' AND pk=1) OR " +
                "(cid=1 AND name='user_id') OR (cid=2 AND name='body') OR (cid=3 AND name='created_at')") == 4
            && Db.Scalar("SELECT count(*) FROM pragma_table_info('users') WHERE (cid=0 AND name='id' AND pk=1) OR " +
                "(cid=1 AND name='username')") == 2
            && Db.Scalar("SELECT count(*) FROM pragma_index_xinfo('posts_created_at_id_idx') WHERE " +
                "(seqno=0 AND name='created_at' AND desc=1) OR (seqno=1 AND name='id' AND desc=1)") == 2
            && Db.Scalar("SELECT count(*) FROM pragma_index_xinfo('likes_post_id_idx') WHERE seqno=0 AND " +
                "name='post_id' AND desc=0") == 1
            && Db.Scalar("SELECT count(*) FROM pragma_encoding WHERE encoding = 'UTF-8'") == 1;
        if (!schema) return false;
        _cookie = (uint)Db.Scalar("PRAGMA schema_version");
        fixed (byte* main = "main\0"u8) _bt = Sqlite.DbNameToBtree(db, main);
        _topKey = Sqlite.KeyInfoAlloc(db, 2, 1);
        _likesKey = Sqlite.KeyInfoAlloc(db, 1, 1);
        if (_bt == 0 || _topKey == 0 || _likesKey == 0) return false;
        _cursorSize = (Sqlite.BtreeCursorSize() + 63) & ~63;
        _cursors = (byte*)NativeMemory.AlignedAlloc((nuint)(CursorCount * _cursorSize), 64);
        NativeMemory.Clear(_cursors, (nuint)(CursorCount * _cursorSize));
        _ids = (long*)NativeMemory.AllocZeroed((FeedSize * 2 + Window) * sizeof(long));
        _counts = _ids + FeedSize;
        _tally = _counts + FeedSize;
        // the search key of likes_post_id_idx: an UnpackedRecord of one integer field, then its Mem
        _search = (byte*)NativeMemory.AllocZeroed(Sqlite.KeySize + Sqlite.MemSize);
        byte* mem = _search + Sqlite.KeySize;
        *(nint*)_search = _likesKey;
        *(byte**)(_search + Sqlite.KeyMem) = mem;
        *(ushort*)(_search + Sqlite.KeyFields) = 1;
        *(sbyte*)(_search + Sqlite.KeyDefaultRc) = 1; // entries with this post_id sort after the key: seek to the first
        *(ushort*)(mem + Sqlite.MemFlags) = Sqlite.MemInt;
        return true;
    }

    /// <summary>Closes the pass's cursors. Call before its COMMIT or ROLLBACK.</summary>
    public static void EndPass()
    {
        _reading = false;
        ulong open = _open;
        if (open == 0) return;
        _open = 0;
        for (int i = 0; i < CursorCount; i++)
            if ((open >> i & 1) != 0) Sqlite.BtreeCloseCursor(_cursors + i * _cursorSize);
    }

    /// <summary>The read transaction of the pass (inside its BEGIN), with the schema as it was at startup.</summary>
    static bool BeginRead()
    {
        if (_reading) return true;
        if (Sqlite.BtreeBeginTrans(_bt, 0, null) != Sqlite.Ok) return false;
        uint cookie;
        Sqlite.BtreeGetMeta(_bt, Sqlite.MetaSchemaVersion, &cookie);
        return _reading = cookie == _cookie;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static byte* Cursor(int i)
    {
        byte* c = _cursors + i * _cursorSize;
        return (_open >> i & 1) != 0 ? c : OpenCursor(i, c);
    }

    static byte* OpenCursor(int i, byte* c)
    {
        uint root = i == Top ? _rootTop : i == Likes ? _rootLikes : i == User || i >= FeedUser ? _rootUsers : _rootPosts;
        nint key = i == Top ? _topKey : i == Likes ? _likesKey : 0;
        Sqlite.BtreeCursorZero(c);
        if (Sqlite.BtreeCursor(_bt, root, 0, key, c) != Sqlite.Ok) return null;
        _open |= 1UL << i;
        return c;
    }

    // ---- records ----

    /// <summary>The record under the cursor when it lies whole on its page and its header length is one byte: that
    /// length, else 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Record(byte* c, out byte* p, out int size)
    {
        // PayloadSize first: it parses the cell, which PayloadFetch only reads back
        uint total = Sqlite.BtreePayloadSize(c);
        uint local;
        p = Sqlite.BtreePayloadFetch(c, &local);
        size = (int)total;
        if (local < total || total < 2) return 0;
        int header = p[0];
        return header < 0x80 && header <= size ? header : 0;
    }

    /// <summary>The next serial type in a record header that ends at <paramref name="header"/>, when it is a varint
    /// of one or two bytes (every column of these tables: a 500-character body takes two); else <see cref="Bad"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Code(byte* p, ref int h, int header)
    {
        if (h >= header) return Bad;
        uint b = p[h];
        if (b < 0x80)
        {
            h++;
            return b;
        }
        if (h + 1 >= header || p[h + 1] >= 0x80) return Bad;
        uint t = (b & 0x7F) << 7 | p[h + 1];
        h += 2;
        return t;
    }

    static ReadOnlySpan<byte> Widths => [0, 1, 2, 3, 4, 6, 8, 8, 0, 0, 0, 0];

    /// <summary>The bytes a column of serial type <paramref name="t"/> takes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Width(uint t) => t >= 12 ? (int)(t - 12 >> 1) : Widths[(int)t];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsInt(uint t) => t - 1 <= 5 || t - 8 <= 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsText(uint t) => t >= 13 && (t & 1) != 0 && t != Bad;

    /// <summary>An integer column of serial type <paramref name="t"/> (1 to 6: big-endian two's complement; 8 and 9:
    /// the constants 0 and 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static long IntAt(byte* p, uint t)
    {
        if (t >= 8) return t - 8;
        int n = Width(t);
        long v = (sbyte)p[0];
        for (int k = 1; k < n; k++) v = v << 8 | p[k];
        return v;
    }

    /// <summary>An entry of posts_created_at_id_idx (created_at, id, rowid): the id.</summary>
    static bool TopId(byte* c, out long id)
    {
        id = 0;
        int header = Record(c, out byte* p, out int size);
        if (header == 0) return false;
        int h = 1;
        uint t0 = Code(p, ref h, header), t1 = Code(p, ref h, header);
        if (!IsText(t0) || !IsInt(t1)) return false;
        int d = header + Width(t0);
        if (d + Width(t1) > size) return false;
        id = IntAt(p + d, t1);
        return true;
    }

    /// <summary>A posts row (id as NULL: the rowid holds it; user_id, body, created_at).</summary>
    static bool PostRow(byte* c, ref Row row)
    {
        int header = Record(c, out byte* p, out int size);
        if (header == 0) return false;
        int h = 1;
        uint t0 = Code(p, ref h, header), t1 = Code(p, ref h, header), t2 = Code(p, ref h, header), t3 = Code(p, ref h, header);
        if (t0 != 0 || !IsInt(t1) || !IsText(t2) || !IsText(t3)) return false;
        int d2 = header + Width(t1), d3 = d2 + Width(t2);
        if (d3 + Width(t3) > size) return false;
        row.UserId = IntAt(p + header, t1);
        row.Body = p + d2;
        row.BodyLength = d3 - d2;
        row.Created = p + d3;
        row.CreatedLength = Width(t3);
        return true;
    }

    /// <summary>A users row (id as NULL, username, ...): the username.</summary>
    static bool UserRow(byte* c, ref Row row)
    {
        int header = Record(c, out byte* p, out int size);
        if (header == 0) return false;
        int h = 1;
        uint t0 = Code(p, ref h, header), t1 = Code(p, ref h, header);
        if (t0 != 0 || !IsText(t1) || header + Width(t1) > size) return false;
        row.Author = p + header;
        row.AuthorLength = Width(t1);
        return true;
    }

    /// <summary>The post under <paramref name="pc"/> and, through <paramref name="uc"/>, its author. False when the
    /// decoder does not cover a row or the author is missing (the SQL join decides then).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool PostAndAuthor(byte* pc, byte* uc, ref Row row)
    {
        int miss;
        return PostRow(pc, ref row)
            && Sqlite.BtreeTableMoveto(uc, row.UserId, 0, &miss) == Sqlite.Ok && miss == 0
            && UserRow(uc, ref row);
    }

    // ---- like counts ----

    /// <summary>Adds the likes of the posts <paramref name="lo"/> to <paramref name="hi"/> to
    /// <paramref name="counts"/>[id - lo], from one walk of likes_post_id_idx.</summary>
    static bool CountLikes(byte* lc, long lo, long hi, long* counts)
    {
        *(long*)(_search + Sqlite.KeySize) = lo;
        int where;
        if (Sqlite.BtreeIndexMoveto(lc, _search, &where) != Sqlite.Ok) return false;
        if (Sqlite.BtreeEof(lc) != 0) return true;
        if (where < 0)
        {
            // on the last entry below lo: the next one, if any, is the first at or above it
            int rc = Sqlite.BtreeNext(lc, 0);
            if (rc == Sqlite.Done) return true;
            if (rc != Sqlite.Ok) return false;
        }
        while (true)
        {
            int header = Record(lc, out byte* p, out int size);
            if (header < 2) return false;
            uint t = p[1];
            if (!IsInt(t) || header + Width(t) > size) return false;
            long id = IntAt(p + header, t);
            if (id > hi) return true;
            if (id >= lo) counts[id - lo]++;
            int rc = Sqlite.BtreeNext(lc, 0);
            if (rc == Sqlite.Done) return true;
            if (rc != Sqlite.Ok) return false;
        }
    }

    // ---- the reads ----

    /// <summary>Writes the /feed body. False, with nothing written, when SQL has to answer.</summary>
    public static bool Feed()
    {
        if (!BeginRead()) return false;
        byte* top = Cursor(Top), lc = Cursor(Likes);
        if (top == null || lc == null) return false;
        long* ids = _ids, counts = _counts;
        // the index is (created_at DESC, id DESC): its first 20 entries are the feed, in order
        int n = 0, empty;
        if (Sqlite.BtreeFirst(top, &empty) != Sqlite.Ok) return false;
        while (empty == 0)
        {
            if (!TopId(top, out ids[n])) return false;
            if (++n == FeedSize) break;
            int rc = Sqlite.BtreeNext(top, 0);
            if (rc == Sqlite.Done) break;
            if (rc != Sqlite.Ok) return false;
        }
        long lo = long.MaxValue, hi = long.MinValue;
        for (int i = 0; i < n; i++)
        {
            lo = Math.Min(lo, ids[i]);
            hi = Math.Max(hi, ids[i]);
        }
        if (n > 0 && (ulong)(hi - lo) < Window)
        {
            long* tally = _tally;
            NativeMemory.Clear(tally, (nuint)(hi - lo + 1) * sizeof(long));
            if (!CountLikes(lc, lo, hi, tally)) return false;
            for (int i = 0; i < n; i++) counts[i] = tally[ids[i] - lo];
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                counts[i] = 0;
                if (!CountLikes(lc, ids[i], ids[i], counts + i)) return false;
            }
        }
        int start = Json.Out.Length;
        Json.Raw("{\"posts\":["u8);
        Row row = default;
        for (int i = 0; i < n; i++)
        {
            byte* pc = Cursor(FeedPost + i), uc = Cursor(FeedUser + i);
            int miss;
            if (pc == null || uc == null || Sqlite.BtreeTableMoveto(pc, ids[i], 0, &miss) != Sqlite.Ok || miss != 0
                || !PostAndAuthor(pc, uc, ref row))
            {
                Json.Out.Length = start;
                return false;
            }
            if (i > 0) Json.Put((byte)',');
            Json.Post(ids[i], row.Body, row.BodyLength, row.Created, row.CreatedLength, row.Author, row.AuthorLength, counts[i]);
        }
        Json.Raw("]}"u8);
        return true;
    }

    /// <summary>Writes the /posts/:id body: <see cref="Found"/>, <see cref="Missing"/> (nothing written) or
    /// <see cref="UseSql"/> (nothing written).</summary>
    public static int GetPost(long id)
    {
        if (!BeginRead()) return UseSql;
        byte* pc = Cursor(Post), uc = Cursor(User), lc = Cursor(Likes);
        if (pc == null || uc == null || lc == null) return UseSql;
        int miss;
        if (Sqlite.BtreeTableMoveto(pc, id, 0, &miss) != Sqlite.Ok) return UseSql;
        if (miss != 0) return Missing;
        Row row = default;
        long count = 0;
        if (!PostAndAuthor(pc, uc, ref row) || !CountLikes(lc, id, id, &count)) return UseSql;
        Json.Raw("{\"post\":"u8);
        Json.Post(id, row.Body, row.BodyLength, row.Created, row.CreatedLength, row.Author, row.AuthorLength, count);
        Json.Put((byte)'}');
        return Found;
    }

    /// <summary>Whether a post with this id exists: <see cref="Found"/>, <see cref="Missing"/> or
    /// <see cref="UseSql"/>.</summary>
    public static int Exists(long id)
    {
        if (!BeginRead()) return UseSql;
        byte* pc = Cursor(Post);
        int miss;
        if (pc == null || Sqlite.BtreeTableMoveto(pc, id, 0, &miss) != Sqlite.Ok) return UseSql;
        return miss == 0 ? Found : Missing;
    }
}
