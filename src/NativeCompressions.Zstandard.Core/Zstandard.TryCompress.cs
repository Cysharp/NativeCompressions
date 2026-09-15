using NativeCompressions.Interop;
using static NativeCompressions.Interop.ZstandardNativeMethods;

namespace NativeCompressions;

public static partial class Zstandard
{
    /// <summary>
    /// Compresses data into the destination buffer.
    /// Returns false when the destination is too small. Other failures throw <see cref="ZstandardException"/>.
    /// </summary>
    public static unsafe bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, int compressionLevel = DefaultCompressionLevel)
    {
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
    public static unsafe bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, in ZstandardCompressionOptions compressionOptions)
    {
        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            var context = ZSTD_createCCtx();
            if (context == null) throw new ZstandardException("Failed to create compression context");

            nuint result;
            try
            {
                compressionOptions.SetParameter(context);
                result = ZSTD_compress2(context, dest, (nuint)destination.Length, src, (nuint)source.Length);
            }
            finally
            {
                ZSTD_freeCCtx(context);
            }

            var ok = TryGetResult(result, out bytesWritten);
            GC.KeepAlive(compressionOptions.Dictionary);
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
    public static unsafe bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, in ZstandardDecompressionOptions decompressionOptions)
    {
        if (decompressionOptions.Dictionary == null)
        {
            return TryDecompress(source, destination, out bytesWritten);
        }

        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            var context = ZSTD_createDCtx();
            if (context == null) throw new ZstandardException("Failed to create decompression context");

            nuint result;
            try
            {
                result = ZSTD_decompress_usingDDict(context, dest, (nuint)destination.Length, src, (nuint)source.Length, decompressionOptions.Dictionary.DecompressionHandle);
            }
            finally
            {
                ZSTD_freeDCtx(context);
            }

            var ok = TryGetResult(result, out bytesWritten);
            GC.KeepAlive(decompressionOptions.Dictionary);
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
