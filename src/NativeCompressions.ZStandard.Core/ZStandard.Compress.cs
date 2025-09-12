using NativeCompressions.ZStandard.Raw;
using System.Buffers;
using System.IO.Pipelines;
using Microsoft.Win32.SafeHandles;
using static NativeCompressions.ZStandard.Raw.NativeMethods;

namespace NativeCompressions.ZStandard;

public static partial class ZStandard
{
    const int AllowParallelCompressThreshold = 1024 * 1024; // 1MB

    /// <summary>
    /// Compresses data using ZStandard algorithm.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> source) => Compress(source, ZStandardCompressionOptions.Default, null);

    /// <summary>
    /// Compresses data using ZStandard algorithm with specified options.
    /// </summary>
    public static unsafe byte[] Compress(ReadOnlySpan<byte> source, in ZStandardCompressionOptions frameOptions, ZStandardCompressionDictionary? dictionary = null)
    {
        var maxLength = ZSTD_compressBound((nuint)source.Length);
        var buffer = ArrayPool<byte>.Shared.Rent((int)maxLength);
        
        try
        {
            fixed (byte* src = source)
            fixed (byte* dest = buffer)
            {
                nuint bytesWritten;
                
                if (dictionary == null)
                {
                    bytesWritten = ZSTD_compress(dest, (nuint)buffer.Length, src, (nuint)source.Length, frameOptions.CompressionLevel);
                }
                else
                {
                    var cctx = ZSTD_createCCtx();
                    if (cctx == null)
                        throw new ZStandardException("Failed to create compression context");
                    
                    try
                    {
                        bytesWritten = ZSTD_compress_usingCDict(cctx, dest, (nuint)buffer.Length, src, (nuint)source.Length, dictionary.CompressionHandle);
                    }
                    finally
                    {
                        ZSTD_freeCCtx(cctx);
                    }
                }
                
                ThrowIfError(bytesWritten);
                return buffer.AsSpan(0, (int)bytesWritten).ToArray();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    /// <summary>
    /// Compresses data into a destination buffer.
    /// </summary>
    public static int Compress(ReadOnlySpan<byte> source, Span<byte> destination) => Compress(source, destination, ZStandardCompressionOptions.Default, null);

    /// <summary>
    /// Compresses data into a destination buffer with specified options.
    /// </summary>
    public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination, in ZStandardCompressionOptions frameOptions, ZStandardCompressionDictionary? dictionary = null)
    {
        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            nuint bytesWritten;
            
            if (dictionary == null)
            {
                bytesWritten = ZSTD_compress(dest, (nuint)destination.Length, src, (nuint)source.Length, frameOptions.CompressionLevel);
            }
            else
            {
                var cctx = ZSTD_createCCtx();
                if (cctx == null)
                    throw new ZStandardException("Failed to create compression context");
                
                try
                {
                    bytesWritten = ZSTD_compress_usingCDict(cctx, dest, (nuint)destination.Length, src, (nuint)source.Length, dictionary.CompressionHandle);
                }
                finally
                {
                    ZSTD_freeCCtx(cctx);
                }
            }
            
            ThrowIfError(bytesWritten);
            return (int)bytesWritten;
        }
    }

    /// <summary>
    /// Asynchronously compresses data from source to destination.
    /// </summary>
    public static async ValueTask CompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZStandardCompressionOptions? frameOptions = null, ZStandardCompressionDictionary? dictionary = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        var options = frameOptions ?? ZStandardCompressionOptions.Default;
        
        // For simplicity, we'll use single-threaded compression for now
        // Multi-threading can be added later using ZSTD's native multi-threading support
        using var encoder = new ZStandardEncoder(options, dictionary);
        
        var buffer = destination.GetMemory(encoder.GetMaxCompressedLength(source.Length));
        var written = encoder.Compress(source.Span, buffer.Span);
        destination.Advance(written);
        
        var finalBuffer = destination.GetMemory(encoder.GetMaxFlushBufferLength(includingFooter: true));
        var finalWritten = encoder.Close(finalBuffer.Span);
        destination.Advance(finalWritten);
        
        await destination.FlushAsync(cancellationToken);
    }

#if NET6_0_OR_GREATER
    /// <summary>
    /// Asynchronously compresses data from a file.
    /// </summary>
    public static ValueTask CompressAsync(SafeFileHandle source, PipeWriter destination, ZStandardCompressionOptions? frameOptions = null, ZStandardCompressionDictionary? dictionary = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        var length = RandomAccess.GetLength(source);
        var memory = new Memory<byte>(new byte[length]);
        RandomAccess.Read(source, memory.Span, 0);
        return CompressAsync(memory, destination, frameOptions, dictionary, maxDegreeOfParallelism, cancellationToken);
    }
#endif

    /// <summary>
    /// Asynchronously compresses data from a stream.
    /// </summary>
    public static async ValueTask CompressAsync(Stream source, PipeWriter destination, ZStandardCompressionOptions? frameOptions = null, ZStandardCompressionDictionary? dictionary = null, CancellationToken cancellationToken = default)
    {
        var options = frameOptions ?? ZStandardCompressionOptions.Default;
        using var encoder = new ZStandardEncoder(options, dictionary);
        
        var buffer = ArrayPool<byte>.Shared.Rent(65536); // 64KB chunks
        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                var destBuffer = destination.GetMemory(encoder.GetMaxCompressedLength(bytesRead));
                var written = encoder.Compress(buffer.AsSpan(0, bytesRead), destBuffer.Span);
                destination.Advance(written);
                await destination.FlushAsync(cancellationToken);
            }
            
            var finalBuffer = destination.GetMemory(encoder.GetMaxFlushBufferLength(includingFooter: true));
            var finalWritten = encoder.Close(finalBuffer.Span);
            destination.Advance(finalWritten);
            await destination.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
