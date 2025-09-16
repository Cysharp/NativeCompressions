using System.Buffers;
using System.IO.Pipelines;
using static NativeCompressions.Zstandard.Raw.NativeMethods;

namespace NativeCompressions.Zstandard;

public static partial class Zstandard
{
    const int AllowParallelCompressThreshold = 1024 * 1024; // 1MB

    /// <summary>
    /// Compresses data using Zstandard algorithm.
    /// </summary>
    public static unsafe byte[] Compress(ReadOnlySpan<byte> source, int compressionLevel = DefaultCompressionLevel)
    {
        var maxLength = GetMaxCompressedLength(source.Length);
        var destination = ArrayPool<byte>.Shared.Rent(maxLength);
        try
        {
            var bytesWritten = Compress(source, destination, compressionLevel);
            return destination.AsSpan(0, bytesWritten).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(destination, clearArray: false);
        }
    }

    /// <summary>
    /// Compresses data using Zstandard algorithm with specified options.
    /// </summary>
    public static unsafe byte[] Compress(ReadOnlySpan<byte> source, in ZstandardCompressionOptions compressionOptions, ZstandardCompressionDictionary? dictionary = null)
    {
        var maxLength = GetMaxCompressedLength(source.Length);
        var destination = ArrayPool<byte>.Shared.Rent(maxLength);
        try
        {
            var bytesWritten = Compress(source, destination, compressionOptions, dictionary);
            return destination.AsSpan(0, bytesWritten).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(destination, clearArray: false);
        }
    }

    public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination, int compressionLevel = DefaultCompressionLevel)
    {
        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            // most simple API
            var bytesWritten = ZSTD_compress(dest, (nuint)destination.Length, src, (nuint)source.Length, compressionLevel);
            ThrowIfError(bytesWritten);
            return (int)bytesWritten;
        }
    }

    public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination, in ZstandardCompressionOptions compressionOptions, ZstandardCompressionDictionary? dictionary = null)
    {
        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            nuint bytesWritten;
            var context = ZSTD_createCCtx();
            if (context == null)
            {
                throw new ZstandardException("Failed to create compression context");
            }

            try
            {
                if (dictionary != null && compressionOptions.IsDefault)
                {
                    bytesWritten = ZSTD_compress_usingCDict(context, dest, (nuint)destination.Length, src, (nuint)source.Length, dictionary.CompressionHandle);
                }
                else
                {
                    compressionOptions.SetParameter(context);
                    dictionary?.SetDictionary(context);
                    bytesWritten = ZSTD_compress2(context, dest, (nuint)destination.Length, src, (nuint)source.Length);
                }
            }
            finally
            {
                ZSTD_freeCCtx(context);
            }

            ThrowIfError(bytesWritten);
            return (int)bytesWritten;
        }
    }

    // TODO: other overload
}
