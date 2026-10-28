# C# with its own io_uring loop (csharp-iouring-xeonfx)

| Item | Value |
|---|---|
| Language | C# 14 on .NET 10 (SDK 10.0.402, pinned in `install.sh`) |
| Server | Own HTTP/1.1 server on its own io_uring loop, called through raw `syscall`. No ASP.NET Core, no liburing |
| Database driver | SQLite 3.54.1 amalgamation built from source by `build.sh` (SHA-256 checked) and linked into the binary, called through `LibraryImport` (`Sqlite.cs`) |
| JSON | Own writer (`Json.cs`); `System.Text.Json`'s `Utf8JsonReader` for request bodies and JWT payloads |
| Build | NativeAOT, `x86-64-v3`, workstation GC (not concurrent) |
| **Nginx or direct** | **Direct**: the server listens on `HOST:PORT` itself |

## How it works

1. One thread owns the rings (`Ring.cs`, `Loop.cs`). Accept is one multishot accept that puts each new socket in the
   ring's table of fixed files (`IORING_FILE_INDEX_ALLOC`), not in the process's fd table, so `LimitNOFILE=65535`
   caps a ring, not the server: the thread opens up to 8 rings of 65,535 slots and moves accept to the next one
   before a table fills (a full table makes the kernel drop the socket it just accepted).
2. Each connection has one multishot receive into a ring of 8,192 provided 2 KB buffers
   (`IORING_REGISTER_PBUF_RING`, or `IORING_OP_PROVIDE_BUFFERS` when the kernel refuses it, as the scorer's
   6.8.0-147 does). A connection that thinks between requests holds no buffer. Each accepted socket gets
   `TCP_QUICKACK=0` through `IORING_OP_URING_CMD` (kernel 6.7 and up), so a request is acked by its response, not
   by a packet of its own after each think time.
3. The thread waits for 256 completions or 8 ms, handles at most 1,024 completions (the rest wait for the next
   pass, so one slow pass does not make the next one longer), commits, then queues one
   send per connection with `MSG_DONTWAIT` from one buffer of the pass. A send the socket does not take whole goes
   on from a buffer of the connection. A close is a `SHUTDOWN` linked to the `CLOSE` of the fixed file. The rings
   use `SINGLE_ISSUER`, `DEFER_TASKRUN` and `COOP_TASKRUN`.
4. The parser (`Http.cs`) reads the request line and the `Authorization`, `Content-Length`, `Connection`,
   `Transfer-Encoding` (400) and `Expect` (100 Continue) headers. Routes are in code.
5. Each endpoint (`Api.cs`) writes the status line, the headers and the JSON body into the pass buffer. Keys are
   fixed bytes, values are SQLite's UTF-8 bytes, escaped only where JSON requires it.
6. The JWT check (`Auth.cs`) runs on every request: HMAC-SHA256 over the token, compared in fixed time, then the
   header, `sub`, `username` and `exp`.

## Rules

- No cache: every request runs its SQL. Nothing remembers rows, responses or JWT results.
- Writes: WAL with `synchronous=NORMAL`. One transaction holds the reads and writes of one pass of the loop
  (group commit), and a response goes out only after the commit of its write.
- `install.sh` (root) installs gcc and the .NET SDK. `build.sh` (normal user) builds SQLite and publishes the server.
  SQLite is built for the box's CPU and profile-guided: an instrumented build first runs `sqlite-train.c`, the
  server's statements in the load's mix on a synthetic database (no seed data), then SQLite is compiled with that
  profile.
- Nothing on the box is tuned. All settings live in the process: SQLite pragmas, GC mode, the listen backlog.

## Checkpoints

A second SQLite connection on its own thread runs `PASSIVE` checkpoints every 50 ms, so their `fsync` calls never
stop the loop. A passive checkpoint never catches up under steady writes, so at 15,000 WAL frames the loop holds
writes (a `POST` waits in its connection while reads go on) until the checkpoint thread has copied every frame and
started the WAL again with a write of its own. After 500 ms the hold ends anyway.

## Credit

The design follows `fsharp-kestrel-delneg`: fixed files past the fd limit, a timed wait for many completions, one
transaction per pass, the checkpoint thread and the write hold. This entry is its own code, compiled ahead of time.
