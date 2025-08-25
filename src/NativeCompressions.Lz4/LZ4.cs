using NativeCompressions.LZ4.Raw;
using System.Runtime.CompilerServices;
using static NativeCompressions.LZ4.Raw.NativeMethods;

namespace NativeCompressions.LZ4;

public static partial class LZ4
{
    static string? version;

    /// <summary>
    /// Gets the version string of the LZ4 library.
    /// </summary>
    public static string Version
    {
        get
        {
            if (version == null)
            {
                unsafe
                {
                    // null-terminated
                    version = new string((sbyte*)LZ4_versionString());
                }
            }
            return version;
        }
    }

    /// <summary>
    /// Gets the version number of the LZ4 library.
    /// </summary>
    public static int VersionNumber => LZ4_versionNumber();

    /// <summary>
    /// Gets the version of the LZ4 frame format supported by the library.
    /// </summary>
    public static uint FrameVersion => LZ4F_getVersion();

    /// <summary>
    /// Get the maximum allowed compression level.
    /// </summary>
    public static int GetMaxCompressionLevel()
    {
        return LZ4F_compressionLevel_max();
    }

    public static int GetMaxCompressedLength(int inputSize, in LZ4FrameOptions options)
    {
        ref var preferences_t = ref Unsafe.As<LZ4FrameOptions, LZ4F_preferences_t>(ref Unsafe.AsRef(in options));
        unsafe
        {
            // calculate bound for LZ4F_compressFrame
            // compressFrameBound uses max header size so changing ContentSize and DictionaryId is ok(in Compress methods, we changes it)
            var bound = LZ4F_compressFrameBound((nuint)inputSize, (LZ4F_preferences_t*)Unsafe.AsPointer(ref preferences_t));
            return (int)bound;
        }
    }

    internal static void HandleErrorCode(nuint code)
    {
        if (LZ4F_isError(code) != 0)
        {
            var error = GetErrorName(code);
            throw new LZ4Exception(error);
        }
    }

    static unsafe string GetErrorName(nuint code)
    {
        var name = (sbyte*)LZ4F_getErrorName(code);
        return new string(name);
    }
}
