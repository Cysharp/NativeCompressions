using System.Reflection;
using System.Runtime.InteropServices;

namespace NativeCompressions.Fuzz;

// Resolves libzstd and lz4 from the runtimes folder of the project reference output, same as the unit tests do.
// Not a module initializer on purpose: the unit test project references this assembly and registers its own resolvers.
public static class NativeLoader
{
    static bool registered;

    public static void EnsureRegistered()
    {
        if (registered) return;
        Register(typeof(Zstandard).Assembly);
        Register(typeof(LZ4).Assembly);
        registered = true;
    }

    static void Register(Assembly assembly)
    {
        try
        {
            NativeLibrary.SetDllImportResolver(assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // a resolver is already registered for the assembly
        }
    }

    static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName is not ("libzstd" or "lz4")) return IntPtr.Zero;

        string platform, prefix, ext;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            platform = "win"; prefix = ""; ext = ".dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            platform = "osx"; prefix = libraryName.StartsWith("lib") ? "" : "lib"; ext = ".dylib";
        }
        else
        {
            platform = "linux"; prefix = libraryName.StartsWith("lib") ? "" : "lib"; ext = ".so";
        }

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            _ => throw new NotSupportedException(),
        };

        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", $"{platform}-{arch}", "native", $"{prefix}{libraryName}{ext}");
        return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
    }
}
