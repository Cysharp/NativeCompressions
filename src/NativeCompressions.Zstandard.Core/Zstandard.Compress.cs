using System.Buffers;
using static NativeCompressions.Interop.ZstandardNativeMethods;

namespace NativeCompressions;

public static partial class Zstandard
{
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
    public static unsafe byte[] Compress(ReadOnlySpan<byte> source, in ZstandardCompressionOptions compressionOptions)
    {
        var maxLength = GetMaxCompressedLength(source.Length);
        var destination = ArrayPool<byte>.Shared.Rent(maxLength);
        try
        {
            var bytesWritten = Compress(source, destination, compressionOptions);
            return destination.AsSpan(0, bytesWritten).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(destination, clearArray: false);
        }
    }

    public static unsafe byte[] Compress(ReadOnlySpan<byte> source, ZstandardEncoder encoder)
    {
        var maxLength = GetMaxCompressedLength(source.Length);
        var destination = ArrayPool<byte>.Shared.Rent(maxLength);
        try
        {
            var bytesWritten = Compress(source, destination, encoder);
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

    public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination, in ZstandardCompressionOptions compressionOptions)
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
                compressionOptions.SetParameter(context);
                bytesWritten = ZSTD_compress2(context, dest, (nuint)destination.Length, src, (nuint)source.Length);
            }
            finally
            {
                ZSTD_freeCCtx(context);
            }

            ThrowIfError(bytesWritten);
            return (int)bytesWritten;
        }
    }

    public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination, ZstandardEncoder encoder)
    {
        var status = encoder.Compress(source, destination, out var bytesConsumed, out var bytesWritten, isFinalBlock: true);
        if (status != OperationStatus.Done)
        {
            throw new ZstandardException($"Compression failed with status: {status}, bytesConsumed: {bytesConsumed}, bytesWritten: {bytesWritten}");
        }
        return bytesWritten;
    }
}
