using System.Runtime.InteropServices;

// Hand written P/Invoke for the smoke run, declared from the C headers (lz4.h, lz4hc.h, lz4frame.h, zstd.h, openzl/*.h).
// The library names are logical names; Program routes them to the explicitly loaded handles.

static unsafe class LZ4Native
{
    const string Lib = "lz4";

    [DllImport(Lib)] public static extern int LZ4_versionNumber();
    [DllImport(Lib)] public static extern byte* LZ4_versionString();
    [DllImport(Lib)] public static extern int LZ4_compressBound(int inputSize);
    [DllImport(Lib)] public static extern int LZ4_compress_default(byte* src, byte* dst, int srcSize, int dstCapacity);
    [DllImport(Lib)] public static extern int LZ4_decompress_safe(byte* src, byte* dst, int compressedSize, int dstCapacity);
    [DllImport(Lib)] public static extern int LZ4_compress_HC(byte* src, byte* dst, int srcSize, int dstCapacity, int compressionLevel);

    public const uint LZ4F_VERSION = 100;
    [DllImport(Lib)] public static extern nuint LZ4F_compressFrameBound(nuint srcSize, void* preferences);
    [DllImport(Lib)] public static extern nuint LZ4F_compressFrame(byte* dst, nuint dstCapacity, byte* src, nuint srcSize, void* preferences);
    [DllImport(Lib)] public static extern nuint LZ4F_createDecompressionContext(void** dctx, uint version);
    [DllImport(Lib)] public static extern nuint LZ4F_freeDecompressionContext(void* dctx);
    [DllImport(Lib)] public static extern nuint LZ4F_decompress(void* dctx, byte* dst, nuint* dstSize, byte* src, nuint* srcSize, void* options);
    [DllImport(Lib)] public static extern uint LZ4F_isError(nuint code);
    [DllImport(Lib)] public static extern byte* LZ4F_getErrorName(nuint code);
}

static unsafe class ZstdNative
{
    const string Lib = "zstd";

    public const int ZSTD_c_nbWorkers = 400;
    public const ulong ZSTD_CONTENTSIZE_UNKNOWN = unchecked(0UL - 1);
    public const ulong ZSTD_CONTENTSIZE_ERROR = unchecked(0UL - 2);

    [DllImport(Lib)] public static extern uint ZSTD_versionNumber();
    [DllImport(Lib)] public static extern byte* ZSTD_versionString();
    [DllImport(Lib)] public static extern nuint ZSTD_compressBound(nuint srcSize);
    [DllImport(Lib)] public static extern nuint ZSTD_compress(byte* dst, nuint dstCapacity, byte* src, nuint srcSize, int compressionLevel);
    [DllImport(Lib)] public static extern nuint ZSTD_decompress(byte* dst, nuint dstCapacity, byte* src, nuint compressedSize);
    [DllImport(Lib)] public static extern ulong ZSTD_getFrameContentSize(byte* src, nuint srcSize);
    [DllImport(Lib)] public static extern uint ZSTD_isError(nuint code);
    [DllImport(Lib)] public static extern byte* ZSTD_getErrorName(nuint code);
    [DllImport(Lib)] public static extern void* ZSTD_createCCtx();
    [DllImport(Lib)] public static extern nuint ZSTD_freeCCtx(void* cctx);
    [DllImport(Lib)] public static extern nuint ZSTD_CCtx_setParameter(void* cctx, int param, int value);
    [DllImport(Lib)] public static extern nuint ZSTD_compress2(void* cctx, byte* dst, nuint dstCapacity, byte* src, nuint srcSize);
}

static unsafe class OpenZLNative
{
    const string Lib = "openzl";

    public const int ZL_CParam_formatVersion = 4;
    public const uint ZL_StandardGraphID_zstd = 7;
    public const uint ZL_StandardGraphID_lz4 = 20;

    // ZL_Report: union of { ZL_ErrorCode code; size_t value } and { ZL_ErrorCode code; ZL_ErrorInfo info (pointer) }.
    [StructLayout(LayoutKind.Sequential)]
    public struct ZL_Report
    {
        public int Code; // ZL_ErrorCode_no_error == 0
        public nuint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ZL_GraphID
    {
        public uint Gid;
    }

    [DllImport(Lib)] public static extern uint ZL_getDefaultEncodingVersion();
    [DllImport(Lib)] public static extern void* ZL_CCtx_create();
    [DllImport(Lib)] public static extern void ZL_CCtx_free(void* cctx);
    [DllImport(Lib)] public static extern void* ZL_Compressor_create();
    [DllImport(Lib)] public static extern void ZL_Compressor_free(void* compressor);
    [DllImport(Lib)] public static extern ZL_Report ZL_Compressor_setParameter(void* compressor, int param, int value);
    [DllImport(Lib)] public static extern ZL_Report ZL_Compressor_selectStartingGraphID(void* compressor, ZL_GraphID graph);
    [DllImport(Lib)] public static extern ZL_Report ZL_CCtx_refCompressor(void* cctx, void* compressor);
    [DllImport(Lib)] public static extern ZL_Report ZL_CCtx_compress(void* cctx, byte* dst, nuint dstCapacity, byte* src, nuint srcSize);
    [DllImport(Lib)] public static extern ZL_Report ZL_getDecompressedSize(byte* compressed, nuint cSize);
    [DllImport(Lib)] public static extern ZL_Report ZL_decompress(byte* dst, nuint dstCapacity, byte* src, nuint srcSize);
}

// Native modules actually mapped into this process, to prove which file a dependency was resolved to.
static class LoadedModules
{
    public static IEnumerable<string> Enumerate()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules) yield return m.FileName;
        }
        else if (OperatingSystem.IsLinux())
        {
            // "address perms offset dev inode path"
            foreach (var line in File.ReadLines("/proc/self/maps"))
            {
                var slash = line.IndexOf('/');
                if (slash >= 0) yield return line[slash..];
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var count = Dyld._dyld_image_count();
            for (uint i = 0; i < count; i++) yield return Marshal.PtrToStringUTF8(Dyld._dyld_get_image_name(i))!;
        }
    }

    static class Dyld
    {
        [DllImport("/usr/lib/libSystem.B.dylib")] public static extern uint _dyld_image_count();
        [DllImport("/usr/lib/libSystem.B.dylib")] public static extern IntPtr _dyld_get_image_name(uint index);
    }
}
