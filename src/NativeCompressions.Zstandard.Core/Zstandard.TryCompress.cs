using NativeCompressions.Interop;
using static NativeCompressions.Interop.ZstandardNativeMethods;

namespace NativeCompressions;

public static partial class Zstandard
{
    /// <summary>
    /// Compresses data into the destination buffer.
    /// Returns false when the destination is too small. Other failures throw <see cref="ZstandardException"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">compressionLevel is outside <see cref="MinCompressionLevel"/> to <see cref="MaxCompressionLevel"/>.</exception>
    public static unsafe bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, int compressionLevel = DefaultCompressionLevel)
    {
        ThrowIfCompressionLevelOutOfRange(compressionLevel, nameof(compressionLevel));
        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            var result = ZSTD_compress(dest, (nuint)destination.Length, src, (nuint)source.Length, compressionLevel);
            return TryGetResult(result, out bytesWritten);
        }
    }

    /// <summary>
    /// Compresses data into the destination buffer with specified options.
    /// Returns false when the destination is too small. Other failures throw <see cref="ZstandardException"/>.
    /// </summary>
    public static unsafe bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, in ZstandardCompressionOptions options)
    {
        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            using var lease = options.AcquireDictionary();
            var context = ZSTD_createCCtx();
            if (context == null) throw new ZstandardException("Failed to create compression context");

            nuint result;
            try
            {
                options.SetParameter(context, lease);
                result = ZSTD_compress2(context, dest, (nuint)destination.Length, src, (nuint)source.Length);
            }
            finally
            {
                ZSTD_freeCCtx(context);
            }

            var ok = TryGetResult(result, out bytesWritten);
            return ok;
        }
    }

    /// <summary>
    /// Decompresses data into the destination buffer.
    /// Returns false when the destination is too small. Other failures throw <see cref="ZstandardException"/>.
    /// </summary>
    public static unsafe bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            var result = ZSTD_decompress(dest, (nuint)destination.Length, src, (nuint)source.Length);
            return TryGetResult(result, out bytesWritten);
        }
    }

    /// <summary>
    /// Decompresses data into the destination buffer with specified options.
    /// Returns false when the destination is too small. Other failures throw <see cref="ZstandardException"/>.
    /// </summary>
    public static unsafe bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, in ZstandardDecompressionOptions options)
    {
        if (options.Dictionary == null)
        {
            return TryDecompress(source, destination, out bytesWritten);
        }

        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            using var lease = options.Dictionary.Acquire();
            var context = ZSTD_createDCtx();
            if (context == null) throw new ZstandardException("Failed to create decompression context");

            nuint result;
            try
            {
                result = ZSTD_decompress_usingDDict(context, dest, (nuint)destination.Length, src, (nuint)source.Length, lease.Decompression);
            }
            finally
            {
                ZSTD_freeDCtx(context);
            }

            var ok = TryGetResult(result, out bytesWritten);
            return ok;
        }
    }

    // dstSize_tooSmall becomes false, any other error throws.
    static bool TryGetResult(nuint result, out int bytesWritten)
    {
        if (IsError(result))
        {
            bytesWritten = 0;
            if (ZSTD_getErrorCode(result) == ZSTD_ErrorCode.ZSTD_error_dstSize_tooSmall)
            {
                return false;
            }
            ThrowIfError(result);
        }

        bytesWritten = (int)result;
        return true;
    }
}
