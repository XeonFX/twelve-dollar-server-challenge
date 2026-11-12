#!/usr/bin/env bash
# Builds as a normal user: SQLite from its pinned source into native/libsqlite3.a, then the server as one NativeAOT
# binary with SQLite linked in, bin/app/server.
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 MSBUILDDISABLENODEREUSE=1
# the SDK runs without ICU (a minimal Ubuntu has none); the app is built with InvariantGlobalization too
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
# For measuring other runtimes (the default build sets none of these): RUNTIME=jit builds for the JIT with SQLite as
# a shared library, DOTNET is another SDK's dotnet, PUBLISH_PROPS are more arguments for dotnet publish.
RUNTIME=${RUNTIME:-aot} DOTNET=${DOTNET:-dotnet}
if [ "$RUNTIME" = jit ]; then LIB=native/libsqlite3.so PIC=(-fPIC); else LIB=native/libsqlite3.a PIC=(-fPIE -fvisibility=hidden); fi

SQLITE=sqlite-amalgamation-3540100   # 3.54.1
SQLITE_SHA256=0a9c8e34352dad46006aa82836d5383bff9dc6bc9ce5a60553c05aa30e6ad1f9
mkdir -p native
if [ ! -f "$LIB" ]; then
  curl -fsSL "https://www.sqlite.org/2026/$SQLITE.zip" -o native/sqlite.zip
  echo "$SQLITE_SHA256  native/sqlite.zip" | sha256sum -c -
  unzip -q -o -j native/sqlite.zip "$SQLITE/sqlite3.c" "$SQLITE/sqlite3.h" -d native
  # SQLite's recommended options, but THREADSAFE=2: two connections, each used by one thread at a time.
  # -DSQLITE_PRIVATE= is the amalgamation's own switch for the linkage of its internal functions: empty, they are
  # no longer static, so the server can call the B-tree layer (sqlite3Btree*) of the unchanged source. The symbols
  # stay hidden (they are linked into the one binary), which leaves SQLite's own calls to them direct and inlinable.
  # Built for this CPU and profile-guided: once instrumented, trained on a synthetic database (sqlite-train.c),
  # then again with the profile.
  CFLAGS=(-O2 -march=native "${PIC[@]}" -fno-semantic-interposition -DSQLITE_PRIVATE=
    -DSQLITE_DQS=0 -DSQLITE_THREADSAFE=2 -DSQLITE_DEFAULT_MEMSTATUS=0
    -DSQLITE_DEFAULT_WAL_SYNCHRONOUS=1 -DSQLITE_LIKE_DOESNT_MATCH_BLOBS -DSQLITE_MAX_EXPR_DEPTH=0
    -DSQLITE_OMIT_DECLTYPE -DSQLITE_OMIT_DEPRECATED -DSQLITE_OMIT_PROGRESS_CALLBACK -DSQLITE_OMIT_SHARED_CACHE
    -DSQLITE_OMIT_AUTOINIT -DSQLITE_OMIT_LOAD_EXTENSION -DSQLITE_USE_ALLOCA -DHAVE_FDATASYNC=1 -DHAVE_USLEEP=1)
  gcc "${CFLAGS[@]}" -fprofile-generate -fprofile-dir="$PWD/native/profile" -c native/sqlite3.c -o native/sqlite3.o
  gcc -O2 -Inative -c sqlite-train.c -o native/train.o
  gcc -fprofile-generate native/train.o native/sqlite3.o -lm -o native/train
  native/train native/train.db
  gcc "${CFLAGS[@]}" -fprofile-use -fprofile-correction -fprofile-dir="$PWD/native/profile" -Wno-missing-profile \
    -c native/sqlite3.c -o native/sqlite3.o
  if [ "$RUNTIME" = jit ]; then gcc -shared -Wl,-Bsymbolic native/sqlite3.o -lm -o "$LIB"; else ar rcs "$LIB" native/sqlite3.o; fi
  rm -rf native/sqlite.zip native/sqlite3.c native/sqlite3.h native/sqlite3.o native/train native/train.o native/profile
fi

rm -rf bin/app
# shellcheck disable=SC2086
"$DOTNET" publish Server.csproj -c Release -r linux-x64 -o bin/app -v q -nologo -p:ServerRuntime="$RUNTIME" ${PUBLISH_PROPS:-}
# the JIT build runs on the runtime of the SDK that built it: start.sh reads where it is
if [ "$RUNTIME" = jit ]; then dirname "$(readlink -f "$(command -v "$DOTNET")")" > bin/app/dotnet-root; fi
# stop the compiler servers, so they keep no memory on a 2 GB box
"$DOTNET" build-server shutdown > /dev/null 2>&1 || true
