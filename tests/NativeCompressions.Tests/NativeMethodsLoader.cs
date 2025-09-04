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

    const string __DllName = "lz4"; // osx, linux = liblz4

    [ModuleInitializer]
    public static void Initialize()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeCompressions.LZ4.LZ4).Assembly, DllImportResolver);
    }

    static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == __DllName)
        {
            var name = __DllName;
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
                prefix = "lib";
                ext = ".dylib";
            }
            else
            {
                platform = "linux";
                prefix = "lib";
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
}
