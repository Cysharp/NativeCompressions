using NativeCompressions.Interop;
using System.Runtime.CompilerServices;
using static NativeCompressions.Interop.ZstandardNativeMethods;

namespace NativeCompressions;

public static partial class Zstandard
{
    static unsafe Zstandard()
    {
        Version = new string((sbyte*)ZSTD_versionString());
        VersionNumber = ZSTD_versionNumber();
        MinCompressionLevel = ZSTD_minCLevel();
        MaxCompressionLevel = ZSTD_maxCLevel();

        var windowLog = ZSTD_cParam_getBounds(ZSTD_cParameter.ZSTD_c_windowLog);
        MinWindowLog = windowLog.lowerBound;
        MaxWindowLog = windowLog.upperBound;
    }

    /// <summary>
    /// Gets the minimum window log (power of 2) accepted by the library.
    /// </summary>
    public static readonly int MinWindowLog;

    /// <summary>
    /// Gets the maximum window log (power of 2) accepted by the library on this platform.
    /// </summary>
    public static readonly int MaxWindowLog;

    /// <summary>
    /// Gets the window log limit a decoder applies by default.
    /// Frames that need a larger window are rejected unless <see cref="ZstandardDecompressionOptions.WindowLogMax"/> is raised.
    /// </summary>
    public const int DefaultWindowLogMax = 27; // ZSTD_WINDOWLOG_LIMIT_DEFAULT

    /// <summary>
    /// Gets the version string of the Zstandard library.
    /// </summary>
    public static readonly string Version;

    /// <summary>
    /// Gets the version number of the Zstandard library.
    /// </summary>
    public static readonly uint VersionNumber;

    /// <summary>
    /// Gets the minimum compression level.
    /// </summary>
    public static readonly int MinCompressionLevel;

    /// <summary>
    /// Gets the maximum compression level.
    /// </summary>
    public static readonly int MaxCompressionLevel;

    /// <summary>
    /// Gets the default compression level.
    /// </summary>
    public const int DefaultCompressionLevel = 3; // ZSTD_defaultCLevel(); need as const.

    public const int MaxFrameHeaderSize = 18; // ZSTD_FRAMEHEADERSIZE_MAX 18

    /// <summary>
    /// Gets the maximum compressed size for a given input size.
    /// </summary>
    public static int GetMaxCompressedLength(int inputSize)
    {
        return checked((int)ZSTD_compressBound((nuint)inputSize));
    }

    /// <summary>
    /// Gets the maximum compressed size for a given input size.
    /// </summary>
    public static nuint GetMaxCompressedLength(nuint inputSize)
    {
        return ZSTD_compressBound(inputSize);
    }

    public static unsafe bool TryGetFrameContentSize(ReadOnlySpan<byte> source, out ulong size)
    {
        const ulong ZSTD_CONTENTSIZE_UNKNOWN = unchecked(0UL - 1);
        const ulong ZSTD_CONTENTSIZE_ERROR = unchecked(0UL - 2);

        // src hint : any size >= `ZSTD_frameHeaderSize_max` is large enough.
        fixed (byte* src = source)
        {
            // @return : -decompressed size of `src` frame content, if known
            // -ZSTD_CONTENTSIZE_UNKNOWN if the size cannot be determined
            // -ZSTD_CONTENTSIZE_ERROR if an error occurred(e.g.invalid magic number, srcSize too small)
            size = ZSTD_getFrameContentSize(src, (nuint)source.Length);

            if (size == ZSTD_CONTENTSIZE_UNKNOWN)
            {
                return false;
            }
            else if (size == ZSTD_CONTENTSIZE_ERROR)
            {
                throw new ZstandardException("Error determining content size(e.g.invalid magic number, srcSize too small)");
            }

            return true;
        }
    }

    /// <summary>
    /// Gets an upper bound of the decompressed size of one or more concatenated frames.
    /// Returns false when the bound cannot be determined, for example for truncated or corrupted input,
    /// or when the frame headers claim a size that does not fit in a long.
    /// </summary>
    public static unsafe bool TryGetMaxDecompressedLength(ReadOnlySpan<byte> source, out long length)
    {
        const ulong ZSTD_CONTENTSIZE_ERROR = unchecked(0UL - 2);

        fixed (byte* src = source)
        {
            var bound = ZSTD_decompressBound(src, (nuint)source.Length);
            if (bound == ZSTD_CONTENTSIZE_ERROR || bound > long.MaxValue)
            {
                length = 0;
                return false;
            }

            length = (long)bound;
            return true;
        }
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ThrowIfError(nuint code)
    {
        if (IsError(code))
        {
            Throw(code);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Throw(nuint code)
        {
            var error = GetErrorName(code);
            throw ZstandardException.FromErrorName(error);
        }
    }
}
