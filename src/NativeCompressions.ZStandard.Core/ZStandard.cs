using NativeCompressions.ZStandard.Raw;
using System.Runtime.CompilerServices;
using static NativeCompressions.ZStandard.Raw.NativeMethods;

namespace NativeCompressions.ZStandard;

public static partial class ZStandard
{
    static string? version;

    /// <summary>
    /// Gets the version string of the ZStandard library.
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
                    version = new string((sbyte*)ZSTD_versionString());
                }
            }
            return version;
        }
    }

    /// <summary>
    /// Gets the version number of the ZStandard library.
    /// </summary>
    public static uint VersionNumber => ZSTD_versionNumber();

    /// <summary>
    /// Gets the minimum compression level.
    /// </summary>
    public static int MinCompressionLevel => ZSTD_minCLevel();

    /// <summary>
    /// Gets the maximum compression level.
    /// </summary>
    public static int MaxCompressionLevel => ZSTD_maxCLevel();

    /// <summary>
    /// Gets the default compression level.
    /// </summary>
    public const int DefaultCompressionLevel = 3; // ZSTD_defaultCLevel();

    /// <summary>
    /// Gets the maximum compressed size for a given input size.
    /// </summary>
    public static int GetMaxCompressedLength(int inputSize)
    {
        return (int)ZSTD_compressBound((nuint)inputSize);
    }

    /// <summary>
    /// Checks if a code represents an error.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsError(nuint code)
    {
        return ZSTD_isError(code) != 0;
    }

    /// <summary>
    /// Gets the error name for a given error code.
    /// </summary>
    internal static unsafe string GetErrorName(nuint code)
    {
        return new string((sbyte*)ZSTD_getErrorName(code));
    }

    /// <summary>
    /// Throws an exception if the result is an error.
    /// </summary>
    internal static void ThrowIfError(nuint code)
    {
        if (IsError(code))
        {
            var error = GetErrorName(code);
            throw new ZStandardException(error);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] // need to check before call IsError
    internal static void ThrowAsError(nuint code)
    {
        var error = GetErrorName(code);
        throw new ZStandardException(error);
    }

    // TODO: remove GetDecompressedSize from here?

    /// <summary>
    /// Gets the decompressed size from a compressed frame.
    /// </summary>
    internal static long GetDecompressedSize(ReadOnlySpan<byte> compressedData)
    {
        unsafe
        {
            fixed (byte* ptr = compressedData)
            {
                var size = ZSTD_getFrameContentSize(ptr, (nuint)compressedData.Length);

                // Check for special return values
                if (size == ulong.MaxValue - 0) // ZSTD_CONTENTSIZE_UNKNOWN
                {
                    return -1;
                }
                if (size == ulong.MaxValue - 1) // ZSTD_CONTENTSIZE_ERROR
                {
                    throw new ZStandardException("Error determining content size");
                }

                return (long)size;
            }
        }
    }
}
