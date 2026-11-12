using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>Only in the JIT build (Server.csproj, ServerRuntime=jit): where the two native libraries are. NativeAOT binds
/// them at link time; the JIT loads them at the first call. "libc" by that name is a linker script where libc6-dev
/// is installed, so it is opened by its soname; SQLite is the shared library beside the server.</summary>
static class JitInterop
{
#pragma warning disable CA2255 // a module initializer in an application
    [ModuleInitializer]
    internal static void Init() =>
        NativeLibrary.SetDllImportResolver(typeof(JitInterop).Assembly, static (name, assembly, path) => name switch
        {
            "libc" => NativeLibrary.Load("libc.so.6"),
            "sqlite3" => NativeLibrary.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "libsqlite3.so")),
            _ => IntPtr.Zero,
        });
#pragma warning restore CA2255
}
