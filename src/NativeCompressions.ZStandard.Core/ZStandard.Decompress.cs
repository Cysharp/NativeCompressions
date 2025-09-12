using NativeCompressions.ZStandard.Raw;
using System.Buffers;
using System.IO.Pipelines;
using Microsoft.Win32.SafeHandles;
using static NativeCompressions.ZStandard.Raw.NativeMethods;

namespace NativeCompressions.ZStandard;

public static partial class ZStandard
{
    /// <summary>
    /// Decompresses ZStandard compressed data.
    /// </summary>
    public static byte[] Decompress(ReadOnlySpan<byte> compressedData, bool trustedData = false, ZStandardCompressionDictionary? dictionary = null)
    {
        if (trustedData)
        {
            // Trust the content size in the frame header if present
            var decompressedSize = GetDecompressedSize(compressedData);
            if (decompressedSize >= 0)
            {
                return DecompressKnownSize(compressedData, (int)decompressedSize, dictionary);
            }
        }
        
        // Unknown size or untrusted data - use streaming decompression
        return DecompressUnknownSize(compressedData, dictionary);
    }

    private static unsafe byte[] DecompressKnownSize(ReadOnlySpan<byte> compressedData, int decompressedSize, ZStandardCompressionDictionary? dictionary)
    {
        var result = new byte[decompressedSize];
        
        fixed (byte* src = compressedData)
        fixed (byte* dest = result)
        {
            nuint bytesWritten;
            
            if (dictionary == null)
            {
                bytesWritten = ZSTD_decompress(dest, (nuint)decompressedSize, src, (nuint)compressedData.Length);
            }
            else
            {
                var dctx = ZSTD_createDCtx();
                if (dctx == null)
                    throw new ZStandardException("Failed to create decompression context");
                
                try
                {
                    bytesWritten = ZSTD_decompress_usingDDict(dctx, dest, (nuint)decompressedSize, src, (nuint)compressedData.Length, dictionary.DecompressionHandle);
                }
                finally
                {
                    ZSTD_freeDCtx(dctx);
                }
            }
            
            ThrowIfError(bytesWritten);
            
            if ((int)bytesWritten != decompressedSize)
            {
                throw new ZStandardException($"Decompressed size mismatch. Expected {decompressedSize}, got {bytesWritten}");
            }
            
            return result;
        }
    }

    private static byte[] DecompressUnknownSize(ReadOnlySpan<byte> compressedData, ZStandardCompressionDictionary? dictionary)
    {
        using var decoder = new ZStandardDecoder(dictionary);
        var buffers = new List<byte[]>();
        var totalSize = 0;
        
        var input = compressedData;
        var outputBuffer = ArrayPool<byte>.Shared.Rent(65536); // 64KB chunks
        
        try
        {
            while (!input.IsEmpty)
            {
                var status = decoder.Decompress(input, outputBuffer, out int bytesConsumed, out int bytesWritten);
                
                if (bytesWritten > 0)
                {
                    var buffer = new byte[bytesWritten];
                    outputBuffer.AsSpan(0, bytesWritten).CopyTo(buffer);
                    buffers.Add(buffer);
                    totalSize += bytesWritten;
                }
                
                input = input.Slice(bytesConsumed);
                
                if (status == OperationStatus.Done)
                    break;
                
                if (status == OperationStatus.NeedMoreData && input.IsEmpty)
                    throw new ZStandardException("Incomplete compressed data");
            }
            
            // Combine all buffers
            var result = new byte[totalSize];
            var offset = 0;
            foreach (var buffer in buffers)
            {
                buffer.CopyTo(result, offset);
                offset += buffer.Length;
            }
            
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(outputBuffer);
        }
    }

    /// <summary>
    /// Decompresses data into a destination buffer.
    /// </summary>
    public static unsafe int Decompress(ReadOnlySpan<byte> compressedData, Span<byte> destination, ZStandardCompressionDictionary? dictionary = null)
    {
        fixed (byte* src = compressedData)
        fixed (byte* dest = destination)
        {
            nuint bytesWritten;
            
            if (dictionary == null)
            {
                bytesWritten = ZSTD_decompress(dest, (nuint)destination.Length, src, (nuint)compressedData.Length);
            }
            else
            {
                var dctx = ZSTD_createDCtx();
                if (dctx == null)
                    throw new ZStandardException("Failed to create decompression context");
                
                try
                {
                    bytesWritten = ZSTD_decompress_usingDDict(dctx, dest, (nuint)destination.Length, src, (nuint)compressedData.Length, dictionary.DecompressionHandle);
                }
                finally
                {
                    ZSTD_freeDCtx(dctx);
                }
            }
            
            ThrowIfError(bytesWritten);
            return (int)bytesWritten;
        }
    }

    /// <summary>
    /// Asynchronously decompresses data from source to destination.
    /// </summary>
    public static async ValueTask DecompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZStandardCompressionDictionary? dictionary = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZStandardDecoder(dictionary);
        
        var input = source;
        var outputBuffer = ArrayPool<byte>.Shared.Rent(65536);
        
        try
        {
            while (!input.IsEmpty)
            {
                var destMemory = destination.GetMemory(outputBuffer.Length);
                var status = decoder.Decompress(input.Span, destMemory.Span, out int bytesConsumed, out int bytesWritten);
                
                if (bytesWritten > 0)
                {
                    destination.Advance(bytesWritten);
                    await destination.FlushAsync(cancellationToken);
                }
                
                input = input.Slice(bytesConsumed);
                
                if (status == OperationStatus.Done)
                    break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(outputBuffer);
        }
    }

#if NET6_0_OR_GREATER
    /// <summary>
    /// Asynchronously decompresses data from a file.
    /// </summary>
    public static ValueTask DecompressAsync(SafeFileHandle source, PipeWriter destination, ZStandardCompressionDictionary? dictionary = null, CancellationToken cancellationToken = default)
    {
        var length = RandomAccess.GetLength(source);
        var memory = new Memory<byte>(new byte[length]);
        RandomAccess.Read(source, memory.Span, 0);
        return DecompressAsync(memory, destination, dictionary, cancellationToken);
    }
#endif

    /// <summary>
    /// Asynchronously decompresses data from a stream.
    /// </summary>
    public static async ValueTask DecompressAsync(Stream source, PipeWriter destination, ZStandardCompressionDictionary? dictionary = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZStandardDecoder(dictionary);
        
        var inputBuffer = ArrayPool<byte>.Shared.Rent(65536);
        var outputBuffer = ArrayPool<byte>.Shared.Rent(65536);
        
        try
        {
            var inputRemaining = Memory<byte>.Empty;
            
            while (true)
            {
                // Read more input if needed
                if (inputRemaining.IsEmpty)
                {
                    var bytesRead = await source.ReadAsync(inputBuffer, 0, inputBuffer.Length, cancellationToken);
                    if (bytesRead == 0)
                        break;
                    
                    inputRemaining = inputBuffer.AsMemory(0, bytesRead);
                }
                
                var destMemory = destination.GetMemory(outputBuffer.Length);
                var status = decoder.Decompress(inputRemaining.Span, destMemory.Span, out int bytesConsumed, out int bytesWritten);
                
                if (bytesWritten > 0)
                {
                    destination.Advance(bytesWritten);
                    await destination.FlushAsync(cancellationToken);
                }
                
                inputRemaining = inputRemaining.Slice(bytesConsumed);
                
                if (status == OperationStatus.Done)
                    break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inputBuffer);
            ArrayPool<byte>.Shared.Return(outputBuffer);
        }
    }
}
