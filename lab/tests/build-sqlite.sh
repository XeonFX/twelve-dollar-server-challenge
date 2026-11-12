#!/usr/bin/env bash
# Builds the pinned SQLite amalgamation as a shared library for the tests, with the entry's switch that exports the
# B-tree functions (-DSQLITE_PRIVATE=). Linux and macOS.
set -euo pipefail
cd "$(dirname "$0")"
SQLITE=sqlite-amalgamation-3540100   # 3.54.1
SQLITE_SHA256=0a9c8e34352dad46006aa82836d5383bff9dc6bc9ce5a60553c05aa30e6ad1f9
mkdir -p native
case "$(uname)" in Darwin) lib=native/libsqlite3.dylib ;; *) lib=native/libsqlite3.so ;; esac
[ -f "$lib" ] && exit 0
curl -fsSL "https://www.sqlite.org/2026/$SQLITE.zip" -o native/sqlite.zip
if command -v sha256sum >/dev/null; then echo "$SQLITE_SHA256  native/sqlite.zip" | sha256sum -c -
else echo "$SQLITE_SHA256  native/sqlite.zip" | shasum -a 256 -c -; fi
unzip -q -o -j native/sqlite.zip "$SQLITE/sqlite3.c" -d native
cc -O1 -g -fPIC -shared -DSQLITE_PRIVATE= -DSQLITE_DQS=0 -DSQLITE_THREADSAFE=2 -DSQLITE_DEFAULT_MEMSTATUS=0 \
  -DSQLITE_DEFAULT_WAL_SYNCHRONOUS=1 -DSQLITE_LIKE_DOESNT_MATCH_BLOBS -DSQLITE_MAX_EXPR_DEPTH=0 -DSQLITE_OMIT_DECLTYPE \
  -DSQLITE_OMIT_DEPRECATED -DSQLITE_OMIT_PROGRESS_CALLBACK -DSQLITE_OMIT_SHARED_CACHE -DSQLITE_OMIT_AUTOINIT \
  -DSQLITE_OMIT_LOAD_EXTENSION -DSQLITE_USE_ALLOCA native/sqlite3.c -o "$lib" -lm
rm -f native/sqlite.zip native/sqlite3.c
