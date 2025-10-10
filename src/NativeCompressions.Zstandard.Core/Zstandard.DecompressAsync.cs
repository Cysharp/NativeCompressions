using Microsoft.Win32.SafeHandles;
using NativeCompressions.Internal;
using System.Buffers;
using System.IO.Pipelines;

namespace NativeCompressions;

public static partial class Zstandard
{
    public static async ValueTask DecompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZstandardDecompressionOptions? options = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(source, destination, decoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask DecompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZstandardDecoder decoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        var status = OperationStatus.DestinationTooSmall;
        while (status == OperationStatus.DestinationTooSmall)
        {
            var dest = destination.GetSpan(requestBufferSize);

            status = decoder.Decompress(source.Span, dest, out var bytesConsumed, out var bytesWritten);
            source = source.Slice(bytesConsumed);

            destination.Advance(bytesWritten);
            await destination.FlushAsync(cancellationToken);
        }

        if (status != OperationStatus.Done)
        {
            throw new ZstandardException($"Zstandard decoder returns {status}.");
        }
    }

    public static async ValueTask DecompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, ZstandardDecompressionOptions? options = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(source, destination, decoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask DecompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, ZstandardDecoder decoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        var status = OperationStatus.DestinationTooSmall;
        var dest = destination.GetSpan(requestBufferSize);
        var writtenInDest = 0;

        // consume all sources
        foreach (var item in source)
        {
            var chunk = item;
            while (chunk.Length > 0) // keep loop if DestinationTooSmall or NeedMoreData
            {
                status = decoder.Decompress(chunk.Span, dest, out var bytesConsumed, out var bytesWritten);
                chunk = chunk.Slice(bytesConsumed);
                dest = dest.Slice(bytesWritten);
                writtenInDest += bytesWritten;

                if (status == OperationStatus.Done)
                {
                    goto END;
                }
                else if (status == OperationStatus.InvalidData)
                {
                    writtenInDest = 0; // no need to flush
                    goto END;
                }

                if (dest.Length == 0)
                {
                    destination.Advance(writtenInDest);
                    await destination.FlushAsync(cancellationToken);
                    dest = destination.GetSpan(requestBufferSize);
                    writtenInDest = 0;
                }
            }
        }

    END:
        if (writtenInDest > 0)
        {
            destination.Advance(writtenInDest);
            await destination.FlushAsync(cancellationToken);
        }

        // flush remaining data in native context
        while (status == OperationStatus.DestinationTooSmall)
        {
            dest = destination.GetSpan(requestBufferSize);
            status = decoder.Decompress([], dest, out _, out var bytesWritten);
            destination.Advance(bytesWritten);
            await destination.FlushAsync(cancellationToken);
        }

        if (status != OperationStatus.Done)
        {
            throw new ZstandardException($"Zstandard decoder returns {status}.");
        }
    }

    // TODO: need s review

    public static ValueTask DecompressAsync(SafeFileHandle source, PipeWriter destination, ZstandardDecompressionOptions? options = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        return DecompressAsync(source, 0, destination, options, requestBufferSize, cancellationToken);
    }

    public static ValueTask DecompressAsync(SafeFileHandle source, PipeWriter destination, ZstandardDecoder decoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        return DecompressAsync(source, 0, destination, decoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask DecompressAsync(SafeFileHandle source, long offset, PipeWriter destination, ZstandardDecompressionOptions? options = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(source, offset, destination, decoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask DecompressAsync(SafeFileHandle source, long offset, PipeWriter destination, ZstandardDecoder decoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
#if NETSTANDARD2_1
        var fs = new FileStream(source, FileAccess.Read, bufferSize: 1, isAsync: true);
        if (offset != 0)
        {
            fs.Position = offset;
        }
        await DecompressAsync(fs, destination, decoder, requestBufferSize, cancellationToken);
#else
        var sourceLength = RandomAccess.GetLength(source);
        var sizeHint = requestBufferSize;

        var sourceBuffer = ArrayPool<byte>.Shared.Rent(sizeHint);
        try
        {
            var status = OperationStatus.DestinationTooSmall;
            var dest = destination.GetSpan(sizeHint);
            var writtenInDest = 0;
            var remaining = sourceLength - offset;

            while (remaining > 0)
            {
                var currentOffset = offset + (sourceLength - remaining);
                var read = await RandomAccess.ReadAsync(source, sourceBuffer, currentOffset, cancellationToken);
                if (read == 0) break; // EOF

                var chunk = sourceBuffer.AsMemory(0, read);
                while (chunk.Length > 0)
                {
                    status = decoder.Decompress(chunk.Span, dest, out var bytesConsumed, out var bytesWritten);
                    chunk = chunk.Slice(bytesConsumed);
                    dest = dest.Slice(bytesWritten);
                    writtenInDest += bytesWritten;

                    if (status == OperationStatus.Done)
                    {
                        goto END;
                    }
                    else if (status == OperationStatus.InvalidData)
                    {
                        writtenInDest = 0;
                        goto END;
                    }

                    if (dest.Length == 0)
                    {
                        destination.Advance(writtenInDest);
                        await destination.FlushAsync(cancellationToken);
                        dest = destination.GetSpan(sizeHint);
                        writtenInDest = 0;
                    }
                }

                remaining -= read;
            }

        END:
            if (writtenInDest > 0)
            {
                destination.Advance(writtenInDest);
                await destination.FlushAsync(cancellationToken);
            }

            // flush remaining data in native context
            while (status == OperationStatus.DestinationTooSmall)
            {
                dest = destination.GetSpan(sizeHint);
                status = decoder.Decompress([], dest, out _, out var bytesWritten);
                destination.Advance(bytesWritten);
                await destination.FlushAsync(cancellationToken);
            }

            if (status != OperationStatus.Done)
            {
                throw new ZstandardException($"Zstandard decoder returns {status}.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBuffer, clearArray: false);
        }
#endif
    }

    public static async ValueTask DecompressAsync(Stream source, PipeWriter destination, ZstandardDecompressionOptions? options = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(source, destination, decoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask DecompressAsync(Stream source, PipeWriter destination, ZstandardDecoder decoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        if (source is MemoryStream ms && ms.TryGetBuffer(out var buffer))
        {
            await DecompressAsync((ReadOnlyMemory<byte>)buffer, destination, decoder, requestBufferSize, cancellationToken);
            return;
        }

#if !NETSTANDARD2_1
        if (source is FileStream fs && fs.CanSeek)
        {
            await DecompressAsync(fs.SafeFileHandle, fs.Position, destination, decoder, requestBufferSize, cancellationToken);
            return;
        }
#endif

        var pipeReader = PipeReader.Create(source, LeaveOpenPipeReaderOptions);
        await DecompressAsync(pipeReader, destination, decoder, requestBufferSize, cancellationToken);
        await pipeReader.CompleteAsync();
    }

    public static async ValueTask DecompressAsync(PipeReader source, PipeWriter destination, ZstandardDecompressionOptions? options = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(source, destination, decoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask DecompressAsync(PipeReader source, PipeWriter destination, ZstandardDecoder decoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        var sizeHint = requestBufferSize;
        var status = OperationStatus.DestinationTooSmall;
        var dest = destination.GetSpan(sizeHint);
        var writtenInDest = 0;

        ReadResult result = default;
        while (!result.IsCompleted)
        {
            result = await source.ReadAsync(cancellationToken);
            if (result.IsCanceled) throw new OperationCanceledException();

            var buffer = result.Buffer;
            foreach (var item in buffer)
            {
                var chunk = item;
                while (chunk.Length > 0)
                {
                    status = decoder.Decompress(chunk.Span, dest, out var bytesConsumed, out var bytesWritten);
                    chunk = chunk.Slice(bytesConsumed);
                    dest = dest.Slice(bytesWritten);
                    writtenInDest += bytesWritten;

                    if (status == OperationStatus.Done)
                    {
                        goto END;
                    }
                    else if (status == OperationStatus.InvalidData)
                    {
                        writtenInDest = 0;
                        goto END;
                    }

                    if (dest.Length == 0)
                    {
                        destination.Advance(writtenInDest);
                        await destination.FlushAsync(cancellationToken);
                        dest = destination.GetSpan(sizeHint);
                        writtenInDest = 0;
                    }
                }
            }
            source.AdvanceTo(buffer.End);
        }

    END:
        if (writtenInDest > 0)
        {
            destination.Advance(writtenInDest);
            await destination.FlushAsync(cancellationToken);
        }

        // flush remaining data in native context
        while (status == OperationStatus.DestinationTooSmall)
        {
            dest = destination.GetSpan(sizeHint);
            status = decoder.Decompress([], dest, out _, out var bytesWritten);
            destination.Advance(bytesWritten);
            await destination.FlushAsync(cancellationToken);
        }

        if (status != OperationStatus.Done)
        {
            throw new ZstandardException($"Zstandard decoder returns {status}.");
        }
    }

    public static async ValueTask DecompressAsync(string sourceFilePath, PipeWriter destination, ZstandardDecompressionOptions? options = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(sourceFilePath, destination, decoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask DecompressAsync(string sourceFilePath, PipeWriter destination, ZstandardDecoder decoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var sourceHandle = File.OpenHandle(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        await DecompressAsync(sourceHandle, destination, decoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask DecompressAsync(string sourceFilePath, string destinationFilePath, ZstandardDecompressionOptions? options = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(sourceFilePath, destinationFilePath, decoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask DecompressAsync(string sourceFilePath, string destinationFilePath, ZstandardDecoder decoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var sourceHandle = File.OpenHandle(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        using var destinationStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.Asynchronous);
        var destinationWriter = PipeWriter.Create(destinationStream);
        await DecompressAsync(sourceHandle, destinationWriter, decoder, requestBufferSize, cancellationToken);
    }
}
