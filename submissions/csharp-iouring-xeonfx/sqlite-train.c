/* The training run for SQLite's profile-guided build (build.sh): a synthetic database with the challenge's schema,
 * then passes of the server's own statements in its request mix (per user: a feed, a post, 15% a like, 2% a new
 * post), one transaction per pass in WAL mode with PASSIVE checkpoints, as the server runs them. No seed data. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "sqlite3.h"

static sqlite3 *db;

static void check(int rc, const char *what)
{
    if (rc != SQLITE_OK && rc != SQLITE_DONE && rc != SQLITE_ROW) {
        fprintf(stderr, "train: %s: %s\n", what, sqlite3_errmsg(db));
        exit(1);
    }
}

static void exec(const char *sql) { check(sqlite3_exec(db, sql, 0, 0, 0), sql); }

static sqlite3_stmt *prepare(const char *sql)
{
    sqlite3_stmt *st;
    check(sqlite3_prepare_v3(db, sql, -1, SQLITE_PREPARE_PERSISTENT, &st, 0), sql);
    return st;
}

/* steps a statement to its end, reading every column as the server does */
static void run(sqlite3_stmt *st)
{
    int rc;
    while ((rc = sqlite3_step(st)) == SQLITE_ROW)
        for (int c = 0; c < sqlite3_column_count(st); c++)
            if (sqlite3_column_type(st, c) == SQLITE_TEXT) sqlite3_column_bytes(st, c), sqlite3_column_text(st, c);
            else sqlite3_column_int64(st, c);
    check(rc, sqlite3_sql(st));
    sqlite3_reset(st);
}

static unsigned rnd(void)
{
    static unsigned long long s = 88172645463325252ULL;
    s ^= s << 13, s ^= s >> 7, s ^= s << 17;
    return (unsigned)s;
}

int main(int argc, char **argv)
{
    if (argc != 2) return fprintf(stderr, "usage: sqlite-train <database file>\n"), 2;
    const char *path = argv[1];
    char extra[512];
    snprintf(extra, sizeof extra, "%s-wal", path), remove(extra);
    snprintf(extra, sizeof extra, "%s-shm", path), remove(extra);
    remove(path);
    sqlite3_initialize(); /* built with SQLITE_OMIT_AUTOINIT */
    check(sqlite3_open(path, &db), "open");

    exec("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA wal_autocheckpoint = 0;"
         "PRAGMA mmap_size = 0; PRAGMA cache_size = -32768; PRAGMA temp_store = MEMORY");
    exec("CREATE TABLE users (id INTEGER PRIMARY KEY, username TEXT NOT NULL UNIQUE,"
         " created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')));"
         "CREATE TABLE posts (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id),"
         " body TEXT NOT NULL CHECK (length(body) BETWEEN 1 AND 500),"
         " created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')));"
         "CREATE TABLE likes (user_id INTEGER NOT NULL REFERENCES users(id), post_id INTEGER NOT NULL REFERENCES posts(id),"
         " created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')), PRIMARY KEY (user_id, post_id));"
         "CREATE INDEX posts_created_at_id_idx ON posts (created_at DESC, id DESC);"
         "CREATE INDEX posts_user_id_idx ON posts (user_id);"
         "CREATE INDEX likes_post_id_idx ON likes (post_id)");

    enum { Users = 20000, Posts = 100000, Likes = 400000 };
    exec("BEGIN");
    sqlite3_stmt *u = prepare("INSERT INTO users (id, username, created_at) VALUES (?1, ?2, '2025-01-01T00:00:00.000Z')");
    for (int i = 1; i <= Users; i++) {
        char name[32];
        snprintf(name, sizeof name, "user_%d_%u", i, rnd() % 10000);
        sqlite3_bind_int(u, 1, i), sqlite3_bind_text(u, 2, name, -1, SQLITE_TRANSIENT), run(u);
    }
    sqlite3_stmt *p = prepare("INSERT INTO posts (id, user_id, body, created_at) VALUES (?1, ?2, ?3, ?4)");
    for (int i = 1; i <= Posts; i++) {
        char body[160], at[32];
        int n = snprintf(body, sizeof body, "post %d says something about %u things", i, rnd());
        for (int len = 20 + rnd() % 120; n < len; n++) body[n] = 'a' + rnd() % 26;
        body[n] = 0;
        snprintf(at, sizeof at, "2025-%02d-%02dT%02d:%02d:%02d.%03dZ", 1 + i / 9000 % 12, 1 + i / 300 % 28,
                 i / 60 % 24, i % 60, i * 7 % 60, i % 1000);
        sqlite3_bind_int(p, 1, i), sqlite3_bind_int(p, 2, 1 + rnd() % Users);
        sqlite3_bind_text(p, 3, body, -1, SQLITE_TRANSIENT), sqlite3_bind_text(p, 4, at, -1, SQLITE_TRANSIENT), run(p);
    }
    sqlite3_stmt *l = prepare("INSERT OR IGNORE INTO likes (user_id, post_id) VALUES (?1, ?2)");
    for (int i = 0; i < Likes; i++)
        sqlite3_bind_int(l, 1, 1 + rnd() % Users), sqlite3_bind_int(l, 2, 1 + rnd() % Posts), run(l);
    exec("COMMIT");
    exec("PRAGMA wal_checkpoint(TRUNCATE)");
    sqlite3_finalize(u), sqlite3_finalize(p), sqlite3_finalize(l);

    /* the server's statements (Db.cs) */
    const char *postSelect = "SELECT p.id, p.body, p.created_at, u.username, "
        "(SELECT count(*) FROM likes l WHERE l.post_id = p.id) FROM posts p JOIN users u ON u.id = p.user_id";
    char sql[512];
    snprintf(sql, sizeof sql, "%s WHERE p.id = ?1", postSelect);
    sqlite3_stmt *post = prepare(sql);
    snprintf(sql, sizeof sql, "%s ORDER BY p.created_at DESC, p.id DESC LIMIT 20", postSelect);
    sqlite3_stmt *feed = prepare(sql);
    sqlite3_stmt *feedIds = prepare("SELECT id FROM posts ORDER BY created_at DESC, id DESC LIMIT 20");
    sqlite3_stmt *feedRange = prepare("SELECT p.id, p.body, p.created_at, u.username FROM posts p "
        "JOIN users u ON u.id = p.user_id WHERE p.id BETWEEN ?1 AND ?2 ORDER BY p.id DESC");
    sqlite3_stmt *likeRange = prepare("SELECT post_id, count(*) FROM likes WHERE post_id BETWEEN ?1 AND ?2 GROUP BY post_id");
    sqlite3_stmt *insertPost = prepare("INSERT INTO posts (user_id, body) VALUES (?1, ?2) RETURNING id, created_at");
    sqlite3_stmt *insertLike = prepare("INSERT INTO likes (user_id, post_id) SELECT ?1, ?2 "
        "WHERE EXISTS (SELECT 1 FROM posts WHERE id = ?2) ON CONFLICT (user_id, post_id) DO NOTHING");
    sqlite3_stmt *begin = prepare("BEGIN"), *commit = prepare("COMMIT");

    long long newest = Posts;
    for (int pass = 0; pass < 3000; pass++) {
        run(begin);
        for (int user = 0; user < 40; user++) {
            /* the feed as the server serves it: the newest 20 ids, then their range, or the plain query */
            long long hi = 0, lo = 0;
            int ids = 0;
            while (sqlite3_step(feedIds) == SQLITE_ROW) {
                long long id = sqlite3_column_int64(feedIds, 0);
                if (!ids++) hi = id;
                lo = id;
            }
            sqlite3_reset(feedIds);
            if (hi - lo < 256) {
                sqlite3_bind_int64(feedRange, 1, lo), sqlite3_bind_int64(feedRange, 2, hi), run(feedRange);
                sqlite3_bind_int64(likeRange, 1, lo), sqlite3_bind_int64(likeRange, 2, hi), run(likeRange);
            } else run(feed);
            long long id = rnd() % 4 ? newest - rnd() % 20 : 1 + rnd() % newest;
            sqlite3_bind_int64(post, 1, id), run(post);
            if (rnd() % 100 < 15)
                sqlite3_bind_int(insertLike, 1, 1 + rnd() % Users), sqlite3_bind_int64(insertLike, 2, id), run(insertLike);
            if (rnd() % 100 < 2) {
                char body[96];
                snprintf(body, sizeof body, "user %u says hi at pass %d", rnd() % Users, pass);
                sqlite3_bind_int(insertPost, 1, 1 + rnd() % Users);
                sqlite3_bind_text(insertPost, 2, body, -1, SQLITE_TRANSIENT), run(insertPost);
                newest++;
            }
        }
        run(commit);
        if (pass % 50 == 49) check(sqlite3_wal_checkpoint_v2(db, 0, SQLITE_CHECKPOINT_PASSIVE, 0, 0), "checkpoint");
    }
    sqlite3_close_v2(db);
    remove(path);
    snprintf(extra, sizeof extra, "%s-wal", path), remove(extra);
    snprintf(extra, sizeof extra, "%s-shm", path), remove(extra);
    return 0;
}
