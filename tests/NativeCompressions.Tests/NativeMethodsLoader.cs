using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// for project reference loader
internal static class NativeMethodsLoader
{
    // https://docs.microsoft.com/en-us/dotnet/standard/native-interop/cross-platform
    // Library path will search
    // win => __DllName, __DllName.dll
    // linux, osx => __DllName.so, __DllName.dylib

    [ModuleInitializer]
    public static void Initialize()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeCompressions.LZ4).Assembly, DllImportResolver);
        NativeLibrary.SetDllImportResolver(typeof(NativeCompressions.Zstandard).Assembly, DllImportResolver);
        NativeLibrary.SetDllImportResolver(typeof(NativeCompressions.Interop.OpenZLNativeMethods).Assembly, DllImportResolver);
    }

    // handle of a bundled native library, for tests that inspect its exports
    internal static IntPtr Load(string libraryName) => DllImportResolver(libraryName, typeof(NativeMethodsLoader).Assembly, null);

    static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName is "lz4" or "libzstd" or "libopenzl")
        {
            var name = libraryName;
            var ext = "";
            var prefix = "";
            var platform = "";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                platform = "win";
                prefix = "";
                ext = ".dll";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                platform = "osx";
                prefix = libraryName.StartsWith("lib") ? "" : "lib";
                ext = ".dylib";
            }
            else
            {
                platform = IsMusl() ? "linux-musl" : "linux";
                prefix = libraryName.StartsWith("lib") ? "" : "lib";
                ext = ".so";
            }

            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                _ => throw new NotSupportedException(),
            };

            return NativeLibrary.Load(Path.Combine($"runtimes/{platform}-{arch}/native/{prefix}{name}{ext}"));
        }

        return IntPtr.Zero;
    }

    // The process runs on musl when musl's dynamic loader (/lib/ld-musl-<arch>.so.1) is mapped.
    // RuntimeInformation.RuntimeIdentifier is not enough: a distro-built .NET reports e.g. alpine.3.22-x64.
    static bool IsMusl() => File.ReadAllText("/proc/self/maps").Contains("/ld-musl-", StringComparison.Ordinal);
}
