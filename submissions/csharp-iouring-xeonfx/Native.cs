using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

/// <summary>The libc calls the server makes: the listening socket, memory maps and the io_uring syscalls (glibc has
/// no wrappers for them, so they go through syscall(2)).</summary>
static unsafe partial class Native
{
    // the values of Linux's headers on x86-64
    public const int EAgain = 11, EIntr = 4, ENoBufs = 105, ECanceled = 125;
    public const long SysIoUringSetup = 425, SysIoUringEnter = 426, SysIoUringRegister = 427;
    const int AfInet = 2, SockStream = 1, SockCloExec = 0x80000, SolSocket = 1, SoReuseAddr = 2;
    const int IpProtoTcp = 6, TcpNoDelay = 1, RlimitNoFile = 7;
    public const int ProtRead = 1, ProtWrite = 2, MapShared = 0x01, MapPrivate = 0x02, MapAnonymous = 0x20;
    public const int MapPopulate = 0x8000;
    public const int OReadOnly = 0, OCloExec = 0x80000, FadvDontNeed = 4;

    [LibraryImport("libc", SetLastError = true)]
    public static partial long syscall(long number, long a1, long a2, long a3, long a4, long a5, long a6);

    /// <summary>syscall(2) without the GC transition, for io_uring_enter calls that do not wait.</summary>
    [LibraryImport("libc", EntryPoint = "syscall")]
    [SuppressGCTransition]
    public static partial long syscall_fast(long number, long a1, long a2, long a3, long a4, long a5, long a6);

    [LibraryImport("libc", SetLastError = true)]
    public static partial void* mmap(void* address, nuint length, int prot, int flags, int fd, long offset);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int socket(int domain, int type, int protocol);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setsockopt(int fd, int level, int name, int* value, int length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int bind(int fd, byte* address, int length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int listen(int fd, int backlog);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int open(byte* path, int flags);

    [LibraryImport("libc")]
    public static partial int posix_fadvise(int fd, long offset, long length, int advice);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int getrlimit(int resource, ulong* limit);

    /// <summary>The errno of the last call above with SetLastError.</summary>
    public static int Errno => Marshal.GetLastPInvokeError();

    /// <summary>The soft RLIMIT_NOFILE (.NET raises it to the hard limit at start), at most 65,535 (systemd's
    /// LimitNOFILE on the box). A ring's table of fixed files can hold that many sockets.</summary>
    public static int MaxFiles()
    {
        ulong* lim = stackalloc ulong[2];
        ulong soft = getrlimit(RlimitNoFile, lim) == 0 ? lim[0] : 65535;
        return (int)Math.Min(soft, 65535UL);
    }

    /// <summary>A listening IPv4 TCP socket on <paramref name="address"/>. TCP_NODELAY is set on it: accepted sockets
    /// inherit it, and a fixed file has no fd to set it on later.</summary>
    public static int Listen(IPAddress address, int port)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) throw new NotSupportedException("HOST must be IPv4");
        int fd = socket(AfInet, SockStream | SockCloExec, 0);
        if (fd < 0) throw new InvalidOperationException($"socket: errno {Errno}");
        int one = 1;
        setsockopt(fd, SolSocket, SoReuseAddr, &one, 4);
        setsockopt(fd, IpProtoTcp, TcpNoDelay, &one, 4);
        byte* sa = stackalloc byte[16];
        new Span<byte>(sa, 16).Clear();
        sa[0] = AfInet;
        sa[2] = (byte)(port >> 8);
        sa[3] = (byte)port;
        address.TryWriteBytes(new Span<byte>(sa + 4, 4), out _);
        if (bind(fd, sa, 16) != 0) throw new InvalidOperationException($"bind {address}:{port}: errno {Errno}");
        // the kernel caps the backlog at somaxconn (4,096); a ramp of new users should not overflow it
        if (listen(fd, 65535) != 0) throw new InvalidOperationException($"listen: errno {Errno}");
        return fd;
    }
}
