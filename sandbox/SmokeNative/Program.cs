using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

// Smoke run for raw native binaries (dll / so / dylib), without NativeCompressions.
// Each library is loaded with plain NativeLibrary.Load(absolutePath) and called through this project's own P/Invoke.
// This proves the built binary itself loads on this machine (arch, OS/glibc version, dependencies) and actually works.
//
//   dotnet run -c Release                                   # repo binaries for the current RID
//   dotnet run -c Release -- --rid osx-x64                  # repo binaries for another RID (e.g. Rosetta)
//   dotnet run -c Release -- --dir ./out/native --libs lz4  # a flat directory (CI artifact, app output, ...)
//   dotnet run -c Release -- --zstd ./libzstd.so            # individual files

SmokeOptions options;
try
{
    options = SmokeOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

// Windows also resolves a DLL's dependencies from PATH, so a DLL that happens to be on a developer's PATH
// (e.g. libgcc_s_seh-1.dll in Git's mingw64\bin) would hide a missing dependency. Emulate a clean machine:
// dependencies must come from the DLL's own directory or the system directories.
// (DllImportSearchPath flags do not help; they do not exclude PATH for an absolute path.)
if (OperatingSystem.IsWindows())
{
    Environment.SetEnvironmentVariable("PATH", string.Join(';', Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
}

return options.IsChild || options.Libs.Count == 1
    ? new SmokeRunner(options).Run()
    : SmokeHost.RunEachInChildProcess(options);

sealed record NativeLib(string Key, string FileBaseName, string RuntimeProject)
{
    // lz4 is shipped as lz4.dll on Windows, everything else has the lib prefix.
    public static readonly NativeLib[] All =
    [
        new("lz4", "lz4", "LZ4"),
        new("zstd", "zstd", "Zstandard"),
        new("openzl", "openzl", "OpenZL"),
    ];

    public string FileName(string rid) => rid.Split('-')[0] switch
    {
        "win" => (Key == "lz4" ? "" : "lib") + FileBaseName + ".dll",
        "osx" => "lib" + FileBaseName + ".dylib",
        "linux" or "android" => "lib" + FileBaseName + ".so",
        var os => throw new NotSupportedException($"'{os}' has no dynamic library (ios/maccatalyst ship static .a; use the Catalyst smoke)."),
    };
}

sealed class SmokeOptions
{
    public string Rid { get; private set; } = RuntimeInformation.RuntimeIdentifier;
    public string? Dir { get; private set; }
    public List<string> Libs { get; } = NativeLib.All.Select(x => x.Key).ToList();
    public Dictionary<string, string> ExplicitPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsChild { get; private set; }

    public static SmokeOptions Parse(string[] args)
    {
        var o = new SmokeOptions();
        var libsGiven = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} requires a value");
            switch (args[i])
            {
                case "--rid": o.Rid = Next(); break;
                case "--dir": o.Dir = Path.GetFullPath(Next()); break;
                case "--libs":
                    o.Libs.Clear();
                    o.Libs.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()));
                    libsGiven = true;
                    break;
                case "--lz4" or "--zstd" or "--openzl": o.ExplicitPaths[args[i][2..]] = Path.GetFullPath(Next()); break;
                case "--child": o.IsChild = true; break;
                default: throw new ArgumentException($"unknown argument: {args[i]}");
            }
        }

        // --lz4/--zstd/--openzl without --libs means "only these".
        if (!libsGiven && o.ExplicitPaths.Count != 0)
        {
            o.Libs.Clear();
            o.Libs.AddRange(NativeLib.All.Select(x => x.Key).Where(o.ExplicitPaths.ContainsKey));
        }

        var unknown = o.Libs.Except(NativeLib.All.Select(x => x.Key)).ToArray();
        if (unknown.Length != 0 || o.Libs.Count == 0) throw new ArgumentException($"--libs must be a subset of lz4,zstd,openzl: {string.Join(",", unknown)}");
        if (o.Dir is not null && o.ExplicitPaths.Count != 0) throw new ArgumentException("--dir and --lz4/--zstd/--openzl are exclusive");

        // Neither --dir nor explicit files: use the repo binaries. Stage them into one flat directory,
        // the same layout an application gets after restoring the NuGet packages (all natives side by side).
        if (o.Dir is null && o.ExplicitPaths.Count == 0 && !o.IsChild) o.Dir = StageRepositoryBinaries(o.Rid);
        return o;
    }

    public string? ExpectedPath(NativeLib lib)
    {
        if (ExplicitPaths.Count != 0) return ExplicitPaths.GetValueOrDefault(lib.Key);
        return Path.Combine(Dir!, lib.FileName(Rid));
    }

    public IEnumerable<string> ToArguments(string lib)
    {
        yield return "--child";
        yield return "--rid"; yield return Rid;
        yield return "--libs"; yield return lib;
        if (Dir is not null) { yield return "--dir"; yield return Dir; }
        foreach (var (key, path) in ExplicitPaths) { yield return "--" + key; yield return path; }
    }

    static string StageRepositoryBinaries(string rid)
    {
        var root = FindRepositoryRoot();
        var stage = Path.Combine(root, "artifacts", "smoke-native", rid);
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        Directory.CreateDirectory(stage);
        foreach (var lib in NativeLib.All)
        {
            var source = Path.Combine(root, "src", $"NativeCompressions.{lib.RuntimeProject}.Runtime", "runtimes", rid, "native", lib.FileName(rid));
            if (File.Exists(source)) File.Copy(source, Path.Combine(stage, lib.FileName(rid)));
        }
        return stage;
    }

    static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NativeCompressions.slnx"))) return dir.FullName;
        }
        throw new ArgumentException("repository root (NativeCompressions.slnx) not found; use --dir or --lz4/--zstd/--openzl.");
    }
}

// Runs every library in its own process: a library loaded earlier must not satisfy another library's dependency
// (e.g. libopenzl needs liblz4; an application that only uses OpenZL never preloads liblz4).
static class SmokeHost
{
    public static int RunEachInChildProcess(SmokeOptions options)
    {
        PrintEnvironment(options);
        var results = new List<(string Lib, int ExitCode)>();
        foreach (var lib in options.Libs)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") psi.ArgumentList.Add(typeof(SmokeHost).Assembly.Location);
            foreach (var a in options.ToArguments(lib)) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi)!;
            process.WaitForExit();
            results.Add((lib, process.ExitCode));
        }

        Console.WriteLine("SUMMARY");
        foreach (var (lib, code) in results) Console.WriteLine($"  {(code == 0 ? "PASS" : "FAIL")} {lib}");
        var failed = results.Count(x => x.ExitCode != 0);
        Console.WriteLine(failed == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failed}/{results.Count} libraries failed)");
        return failed == 0 ? 0 : 1;
    }

    public static void PrintEnvironment(SmokeOptions options)
    {
        Console.WriteLine($"OS          : {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Runtime     : {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier}, process {RuntimeInformation.ProcessArchitecture})");
        Console.WriteLine($"Target RID  : {options.Rid}");
        Console.WriteLine($"Native from : {options.Dir ?? string.Join(", ", options.ExplicitPaths.Values)}");
        if (OperatingSystem.IsWindows()) Console.WriteLine($"PATH        : {Environment.GetEnvironmentVariable("PATH")} (reduced to emulate a clean machine)");
        Console.WriteLine();
    }
}

sealed class SmokeRunner(SmokeOptions options)
{
    readonly Dictionary<string, IntPtr> handles = new();
    int failed;

    public int Run()
    {
        if (!options.IsChild) SmokeHost.PrintEnvironment(options);

        // Route the lz4/zstd/openzl DllImport names to the handles loaded below; they never fall back to the default probing.
        // Other names (e.g. libSystem for the dyld image list) use the default probing.
        NativeLibrary.SetDllImportResolver(typeof(SmokeRunner).Assembly, (name, _, _) =>
            handles.TryGetValue(name, out var h) ? h
            : NativeLib.All.Any(x => x.Key == name) ? throw new DllNotFoundException($"'{name}' was not loaded by the smoke run.")
            : IntPtr.Zero);

        foreach (var lib in NativeLib.All.Where(x => options.Libs.Contains(x.Key)))
        {
            Console.WriteLine($"[{lib.Key}]");
            if (Check("load", () => Load(lib)))
            {
                switch (lib.Key)
                {
                    case "lz4": RunLZ4(); break;
                    case "zstd": RunZstd(); break;
                    case "openzl": RunOpenZL(); break;
                }
                Check("dependencies", VerifyLoadedModules);
            }
            Console.WriteLine(failed == 0 ? "  => PASS" : $"  => FAIL ({failed} check(s) failed)");
            Console.WriteLine();
        }
        return failed == 0 ? 0 : 1;
    }

    bool Check(string name, Func<string?> action)
    {
        try
        {
            var detail = action();
            Console.WriteLine($"  PASS {name}{(detail is null ? "" : $": {detail}")}");
            return true;
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"  FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    string Load(NativeLib lib)
    {
        var path = options.ExpectedPath(lib) ?? throw new ArgumentException($"no path for {lib.Key}");
        if (!File.Exists(path)) throw new FileNotFoundException($"not found: {path}");
        try
        {
            handles[lib.Key] = NativeLibrary.Load(path);
        }
        catch (DllNotFoundException) when (OperatingSystem.IsWindows())
        {
            var imports = PeImports.Read(path);
            var dir = Path.GetDirectoryName(path)!;
            var missing = imports.Where(x => !File.Exists(Path.Combine(dir, x)) && !File.Exists(Path.Combine(Environment.SystemDirectory, x)) && !x.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase));
            throw new DllNotFoundException($"{path} failed to load. imports: [{string.Join(", ", imports)}], not found next to it or in System32: [{string.Join(", ", missing)}]");
        }
        return path;
    }

    // Every lz4/zstd/openzl native module in the process must be one of the files given to the smoke run.
    // Catches a dependency silently resolved to another copy (e.g. the OS's /usr/lib/.../liblz4.so.1 or Homebrew's dylib).
    string VerifyLoadedModules()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var expected = NativeLib.All.Select(options.ExpectedPath).OfType<string>().Select(Path.GetFullPath).ToHashSet(comparer);
        var pattern = new Regex(@"^(lib)?(lz4|zstd|openzl)(\.\d+)*\.(dll|so|dylib)(\.\d+)*$", RegexOptions.IgnoreCase);
        var loaded = LoadedModules.Enumerate().Where(x => pattern.IsMatch(Path.GetFileName(x))).Select(Path.GetFullPath).Distinct(comparer).ToArray();

        var foreign = loaded.Where(x => !expected.Contains(x)).ToArray();
        if (foreign.Length != 0) throw new InvalidOperationException($"loaded from outside the given files: [{string.Join(", ", foreign)}]");
        return string.Join(", ", loaded.Select(Path.GetFileName));
    }

    unsafe void RunLZ4()
    {
        Check("version", () => $"{Str(LZ4Native.LZ4_versionString())} ({LZ4Native.LZ4_versionNumber()})");
        foreach (var data in TestData.All)
        {
            Check($"block roundtrip ({data})", () =>
            {
                var compressed = Buf(checked((int)(LZ4Native.LZ4_compressBound(data.Bytes.Length))));
                var size = LZ4Native.LZ4_compress_default(Ptr(data.Bytes), Ptr(compressed), data.Bytes.Length, compressed.Length);
                if (size <= 0) throw new InvalidOperationException($"LZ4_compress_default returned {size}");
                return DecompressBlock(data.Bytes, compressed, size);
            });
            Check($"hc block roundtrip ({data})", () =>
            {
                var compressed = Buf(checked((int)(LZ4Native.LZ4_compressBound(data.Bytes.Length))));
                var size = LZ4Native.LZ4_compress_HC(Ptr(data.Bytes), Ptr(compressed), data.Bytes.Length, compressed.Length, 9);
                if (size <= 0) throw new InvalidOperationException($"LZ4_compress_HC returned {size}");
                return DecompressBlock(data.Bytes, compressed, size);
            });
            Check($"frame roundtrip ({data})", () =>
            {
                var compressed = Buf(checked((int)(LZ4Native.LZ4F_compressFrameBound((nuint)data.Bytes.Length, null))));
                var size = LZ4F(LZ4Native.LZ4F_compressFrame(Ptr(compressed), (nuint)compressed.Length, Ptr(data.Bytes), (nuint)data.Bytes.Length, null));

                void* dctx;
                LZ4F(LZ4Native.LZ4F_createDecompressionContext(&dctx, LZ4Native.LZ4F_VERSION));
                try
                {
                    var decompressed = Buf(checked((int)(data.Bytes.Length)));
                    nuint srcPos = 0, dstPos = 0, hint = 1;
                    while (hint != 0)
                    {
                        nuint srcSize = size - srcPos, dstSize = (nuint)decompressed.Length - dstPos;
                        hint = LZ4F(LZ4Native.LZ4F_decompress(dctx, Ptr(decompressed) + dstPos, &dstSize, Ptr(compressed) + srcPos, &srcSize, null));
                        srcPos += srcSize;
                        dstPos += dstSize;
                        if (srcSize == 0 && dstSize == 0 && hint != 0) throw new InvalidDataException("LZ4F_decompress made no progress");
                    }
                    return AssertEqual(data.Bytes, decompressed.AsSpan(0, (int)dstPos), (int)size);
                }
                finally
                {
                    LZ4Native.LZ4F_freeDecompressionContext(dctx);
                }
            });
        }

        static string DecompressBlock(byte[] original, byte[] compressed, int compressedSize)
        {
            var decompressed = Buf(checked((int)(original.Length)));
            var size = LZ4Native.LZ4_decompress_safe(Ptr(compressed), Ptr(decompressed), compressedSize, decompressed.Length);
            if (size < 0) throw new InvalidDataException($"LZ4_decompress_safe returned {size}");
            return AssertEqual(original, decompressed.AsSpan(0, size), compressedSize);
        }

        static nuint LZ4F(nuint code) => LZ4Native.LZ4F_isError(code) != 0 ? throw new InvalidOperationException(Str(LZ4Native.LZ4F_getErrorName(code))) : code;
    }

    unsafe void RunZstd()
    {
        Check("version", () => $"{Str(ZstdNative.ZSTD_versionString())} ({ZstdNative.ZSTD_versionNumber()})");
        foreach (var data in TestData.All)
        {
            Check($"roundtrip ({data})", () =>
            {
                var compressed = Buf(checked((int)(ZstdNative.ZSTD_compressBound((nuint)data.Bytes.Length))));
                var size = Zstd(ZstdNative.ZSTD_compress(Ptr(compressed), (nuint)compressed.Length, Ptr(data.Bytes), (nuint)data.Bytes.Length, 3));
                return Decompress(data.Bytes, compressed, size);
            });
        }

        // NativeCompressions exposes NbWorkers, which needs a ZSTD_MULTITHREAD build; a single-threaded build rejects nbWorkers > 0.
        Check("multithread roundtrip (nbWorkers=2)", () =>
        {
            var data = TestData.All[^1].Bytes;
            var cctx = ZstdNative.ZSTD_createCCtx();
            try
            {
                var code = ZstdNative.ZSTD_CCtx_setParameter(cctx, ZstdNative.ZSTD_c_nbWorkers, 2);
                if (ZstdNative.ZSTD_isError(code) != 0) throw new InvalidOperationException($"nbWorkers rejected ({Str(ZstdNative.ZSTD_getErrorName(code))}); not built with ZSTD_MULTITHREAD");

                var compressed = Buf(checked((int)(ZstdNative.ZSTD_compressBound((nuint)data.Length))));
                var size = Zstd(ZstdNative.ZSTD_compress2(cctx, Ptr(compressed), (nuint)compressed.Length, Ptr(data), (nuint)data.Length));
                return Decompress(data, compressed, size);
            }
            finally
            {
                ZstdNative.ZSTD_freeCCtx(cctx);
            }
        });

        static string Decompress(byte[] original, byte[] compressed, nuint compressedSize)
        {
            var contentSize = ZstdNative.ZSTD_getFrameContentSize(Ptr(compressed), compressedSize);
            if (contentSize != (ulong)original.Length) throw new InvalidDataException($"ZSTD_getFrameContentSize returned {contentSize}, expected {original.Length}");
            var decompressed = Buf(checked((int)(original.Length)));
            var size = Zstd(ZstdNative.ZSTD_decompress(Ptr(decompressed), (nuint)decompressed.Length, Ptr(compressed), compressedSize));
            return AssertEqual(original, decompressed.AsSpan(0, (int)size), (int)compressedSize);
        }

        static nuint Zstd(nuint code) => ZstdNative.ZSTD_isError(code) != 0 ? throw new InvalidOperationException(Str(ZstdNative.ZSTD_getErrorName(code))) : code;
    }

    unsafe void RunOpenZL()
    {
        var version = 0u;
        Check("encoding version", () => (version = OpenZLNative.ZL_getDefaultEncodingVersion()).ToString());
        foreach (var (graphName, graph) in new[] { ("zstd", OpenZLNative.ZL_StandardGraphID_zstd), ("lz4", OpenZLNative.ZL_StandardGraphID_lz4) })
        {
            foreach (var data in TestData.All)
            {
                Check($"{graphName} graph roundtrip ({data})", () =>
                {
                    var cctx = OpenZLNative.ZL_CCtx_create();
                    var compressor = OpenZLNative.ZL_Compressor_create();
                    try
                    {
                        ZL(OpenZLNative.ZL_Compressor_setParameter(compressor, OpenZLNative.ZL_CParam_formatVersion, (int)version));
                        ZL(OpenZLNative.ZL_Compressor_selectStartingGraphID(compressor, new OpenZLNative.ZL_GraphID { Gid = graph }));
                        ZL(OpenZLNative.ZL_CCtx_refCompressor(cctx, compressor));

                        // ZL_compressBound is ZL_INLINE (not exported); twice the input is far above it.
                        var compressed = Buf(checked((int)(data.Bytes.Length * 2 + 1024)));
                        var size = ZL(OpenZLNative.ZL_CCtx_compress(cctx, Ptr(compressed), (nuint)compressed.Length, Ptr(data.Bytes), (nuint)data.Bytes.Length));

                        var contentSize = ZL(OpenZLNative.ZL_getDecompressedSize(Ptr(compressed), size));
                        if (contentSize != (nuint)data.Bytes.Length) throw new InvalidDataException($"ZL_getDecompressedSize returned {contentSize}, expected {data.Bytes.Length}");
                        var decompressed = Buf(checked((int)(data.Bytes.Length)));
                        var written = ZL(OpenZLNative.ZL_decompress(Ptr(decompressed), (nuint)decompressed.Length, Ptr(compressed), size));
                        return AssertEqual(data.Bytes, decompressed.AsSpan(0, (int)written), (int)size);
                    }
                    finally
                    {
                        OpenZLNative.ZL_Compressor_free(compressor);
                        OpenZLNative.ZL_CCtx_free(cctx);
                    }
                });
            }
        }

        static nuint ZL(OpenZLNative.ZL_Report r) => r.Code != 0 ? throw new InvalidOperationException($"OpenZL error code {r.Code}") : r.Value;
    }

    // Every buffer passed to native code is allocated on the pinned object heap (Buf / TestData), so a raw pointer stays valid.
    // GetArrayDataReference also gives a non-null pointer for an empty array, unlike `fixed`.
    static unsafe byte* Ptr(byte[] array) => (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));

    static byte[] Buf(int size) => GC.AllocateUninitializedArray<byte>(size, pinned: true);

    static unsafe string Str(byte* p) => Marshal.PtrToStringUTF8((IntPtr)p) ?? "";

    static string AssertEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, int compressedLength)
    {
        if (!expected.SequenceEqual(actual)) throw new InvalidDataException($"roundtrip mismatch (expected {expected.Length} bytes, got {actual.Length})");
        return $"{expected.Length} -> {compressedLength} bytes";
    }
}

// Minimal PE import table reader, used only to explain a failed load on Windows.
static class PeImports
{
    public static string[] Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var headers = pe.PEHeaders;
        var image = pe.GetEntireImage().GetContent().ToArray();

        int ToOffset(int rva)
        {
            foreach (var s in headers.SectionHeaders)
            {
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData)) return rva - s.VirtualAddress + s.PointerToRawData;
            }
            throw new BadImageFormatException($"rva 0x{rva:x} is not in any section");
        }

        var dir = headers.PEHeader!.ImportTableDirectory;
        if (dir.RelativeVirtualAddress == 0) return [];
        var result = new List<string>();
        for (var offset = ToOffset(dir.RelativeVirtualAddress); ; offset += 20) // sizeof(IMAGE_IMPORT_DESCRIPTOR)
        {
            var nameRva = BitConverter.ToInt32(image, offset + 12);
            if (nameRva == 0) break;
            var nameOffset = ToOffset(nameRva);
            var end = Array.IndexOf(image, (byte)0, nameOffset);
            result.Add(System.Text.Encoding.ASCII.GetString(image, nameOffset, end - nameOffset));
        }
        return result.ToArray();
    }
}

sealed record TestData(string Name, byte[] Bytes)
{
    public static readonly TestData[] All =
    [
        new("empty", Pinned(0)),
        new("text", CreateText(64 * 1024)),
        new("mixed", CreateMixed(4 * 1024 * 1024)),
    ];

    public override string ToString() => $"{Name}, {Bytes.Length} bytes";

    static byte[] CreateText(int size)
    {
        var line = "NativeCompressions smoke run: the quick brown fox jumps over the lazy dog 0123456789\n"u8;
        var bytes = Pinned(size);
        for (var i = 0; i < size; i++) bytes[i] = line[i % line.Length];
        return bytes;
    }

    // Compressible text interleaved with incompressible random blocks, deterministic across runs.
    static byte[] CreateMixed(int size)
    {
        var bytes = CreateText(size);
        var random = new Random(12345);
        for (var offset = 0; offset < size; offset += 64 * 1024)
        {
            random.NextBytes(bytes.AsSpan(offset, Math.Min(16 * 1024, size - offset)));
        }
        return bytes;
    }

    static byte[] Pinned(int size) => GC.AllocateUninitializedArray<byte>(size, pinned: true);
}
