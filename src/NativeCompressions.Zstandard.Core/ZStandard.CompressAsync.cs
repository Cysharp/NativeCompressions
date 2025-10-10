using Microsoft.Win32.SafeHandles;
using NativeCompressions.Internal;
using System.Buffers;
using System.IO.Pipelines;

namespace NativeCompressions;

public static partial class Zstandard
{
    const int MinimumBufferSize = 65536;
    static readonly StreamPipeReaderOptions LeaveOpenPipeReaderOptions = new StreamPipeReaderOptions(leaveOpen: true);

    public static async ValueTask CompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZstandardCompressionOptions? options = null, int? maxDegreeOfParallelism = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options, maxDegreeOfParallelism);
        await CompressAsync(source, destination, encoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask CompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZstandardEncoder encoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        var sizeHint = GetBufferSize(source.Length, requestBufferSize);

        var status = OperationStatus.DestinationTooSmall;
        while (status != OperationStatus.Done)
        {
            var dest = destination.GetSpan(sizeHint);

            status = encoder.Compress(source.Span, dest, out var bytesConsumed, out var bytesWritten, isFinalBlock: true);
            source = source.Slice(bytesConsumed);

            if (status == OperationStatus.InvalidData)
            {
                throw new ZstandardException("ZstandardEncoder returns InvalidData.");
            }

            destination.Advance(bytesWritten);
            await destination.FlushAsync(cancellationToken);
        }
    }

    public static async ValueTask CompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, ZstandardCompressionOptions? options = null, int? maxDegreeOfParallelism = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options, maxDegreeOfParallelism);
        await CompressAsync(source, destination, encoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask CompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, ZstandardEncoder encoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        var sizeHint = GetBufferSize(source.Length, requestBufferSize);
        var dest = destination.GetSpan(sizeHint);
        var writtenInDest = 0;

        foreach (var item in source)
        {
            var chunk = item;
            var status = OperationStatus.DestinationTooSmall;
            while (status != OperationStatus.Done) // when chunk is fully consumed, go to next chunk
            {
                status = encoder.Compress(chunk.Span, dest, out var bytesConsumed, out var bytesWritten, isFinalBlock: false); // not guarantees finalBlock
                chunk = chunk.Slice(bytesConsumed);
                dest = dest.Slice(bytesWritten);
                writtenInDest += bytesWritten;

                if (status == OperationStatus.InvalidData)
                {
                    throw new ZstandardException("ZstandardEncoder returns InvalidData.");
                }

                if (dest.Length == 0)
                {
                    destination.Advance(writtenInDest);
                    await destination.FlushAsync(cancellationToken);

                    writtenInDest = 0;
                    dest = destination.GetSpan(sizeHint);
                }
            }
        }

        if (writtenInDest != 0)
        {
            destination.Advance(writtenInDest);
            await destination.FlushAsync(cancellationToken);
        }

        // write final block
        {
            var status = OperationStatus.DestinationTooSmall;
            while (status != OperationStatus.Done)
            {
                dest = destination.GetSpan(sizeHint);
                status = encoder.Close(dest, out var bytesWritten);

                if (status == OperationStatus.InvalidData)
                {
                    throw new ZstandardException("ZstandardEncoder.Close returns InvalidData.");
                }

                destination.Advance(bytesWritten);
                await destination.FlushAsync(cancellationToken);
            }
        }
    }

    public static ValueTask CompressAsync(SafeFileHandle source, PipeWriter destination, ZstandardCompressionOptions? options = null, int? maxDegreeOfParallelism = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        return CompressAsync(source, 0, destination, options, maxDegreeOfParallelism, requestBufferSize, cancellationToken);
    }

    public static ValueTask CompressAsync(SafeFileHandle source, PipeWriter destination, ZstandardEncoder encoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        return CompressAsync(source, 0, destination, encoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask CompressAsync(SafeFileHandle source, long offset, PipeWriter destination, ZstandardCompressionOptions? options = null, int? maxDegreeOfParallelism = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options, maxDegreeOfParallelism);
        await CompressAsync(source, offset, destination, encoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask CompressAsync(SafeFileHandle source, long offset, PipeWriter destination, ZstandardEncoder encoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
#if NETSTANDARD2_1
        // don't use `using` to keep source SafeFileHandle open 
        var fs = new FileStream(source, FileAccess.Read, bufferSize: 1, isAsync: true);
        if (offset != 0)
        {
            fs.Position = offset;
        }
        await CompressAsync(fs, destination, encoder, requestBufferSize, cancellationToken);
#else
        var sourceLength = RandomAccess.GetLength(source);
        var sizeHint = GetBufferSize(sourceLength, requestBufferSize);

        var sourceBuffer = ArrayPool<byte>.Shared.Rent(sizeHint);
        try
        {
            var writtenInDest = 0;
            var dest = destination.GetMemory(sizeHint);
            var remaining = sourceLength - offset;
            while (remaining != 0)
            {
                var currentOffset = offset + (sourceLength - remaining);
                var read = await RandomAccess.ReadAsync(source, sourceBuffer, currentOffset, cancellationToken);
                var sourceMemory = sourceBuffer.AsMemory(0, read);

                var status = OperationStatus.DestinationTooSmall;
                while (status != OperationStatus.Done)
                {
                    status = encoder.Compress(sourceMemory.Span, dest.Span, out var bytesConsumed, out var bytesWritten, isFinalBlock: false); // not guarantees finalBlock
                    sourceMemory = sourceMemory.Slice(bytesConsumed);
                    dest = dest.Slice(bytesWritten);
                    writtenInDest += bytesWritten;

                    if (status == OperationStatus.InvalidData)
                    {
                        throw new ZstandardException("ZstandardEncoder returns InvalidData.");
                    }

                    if (dest.Length == 0)
                    {
                        destination.Advance(writtenInDest);
                        await destination.FlushAsync(cancellationToken);

                        writtenInDest = 0;
                        dest = destination.GetMemory(sizeHint);
                    }
                }

                remaining -= read;
            }

            if (writtenInDest != 0)
            {
                destination.Advance(writtenInDest);
                await destination.FlushAsync(cancellationToken);
            }

            // write final block
            {
                var status = OperationStatus.DestinationTooSmall;
                while (status != OperationStatus.Done)
                {
                    dest = destination.GetMemory(sizeHint);
                    status = encoder.Close(dest.Span, out var bytesWritten);

                    if (status == OperationStatus.InvalidData)
                    {
                        throw new ZstandardException("ZstandardEncoder.Close returns InvalidData.");
                    }

                    destination.Advance(bytesWritten);
                    await destination.FlushAsync(cancellationToken);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBuffer, clearArray: false);
        }
#endif
    }

    public static async ValueTask CompressAsync(Stream source, PipeWriter destination, ZstandardCompressionOptions? options = null, int? maxDegreeOfParallelism = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options, maxDegreeOfParallelism);
        await CompressAsync(source, destination, encoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask CompressAsync(Stream source, PipeWriter destination, ZstandardEncoder encoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        if (source is MemoryStream ms && ms.TryGetBuffer(out var buffer))
        {
            await CompressAsync((ReadOnlyMemory<byte>)buffer, destination, encoder, requestBufferSize, cancellationToken);
            return;
        }

#if !NETSTANDARD2_1
        if (source is FileStream fs && fs.CanSeek)
        {
            await CompressAsync(fs.SafeFileHandle, fs.Position, destination, encoder, requestBufferSize, cancellationToken);
            return;
        }
#endif

        var pipeReader = PipeReader.Create(source, LeaveOpenPipeReaderOptions);
        await CompressAsync(pipeReader, destination, encoder, requestBufferSize, cancellationToken);
        await pipeReader.CompleteAsync();
    }

    public static async ValueTask CompressAsync(PipeReader source, PipeWriter destination, ZstandardCompressionOptions? options = null, int? maxDegreeOfParallelism = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options, maxDegreeOfParallelism);
        await CompressAsync(source, destination, encoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask CompressAsync(PipeReader source, PipeWriter destination, ZstandardEncoder encoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        var sizeHint = requestBufferSize;

        var writtenInDest = 0;
        var dest = destination.GetMemory(sizeHint);

        ReadResult result = default;
        while (!result.IsCompleted)
        {
            result = await source.ReadAsync(cancellationToken);
            if (result.IsCanceled) throw new OperationCanceledException();

            var buffer = result.Buffer;
            foreach (var item in buffer)
            {
                var chunk = item;
                var status = OperationStatus.DestinationTooSmall;
                while (status != OperationStatus.Done) // when chunk is fully consumed, go to next chunk
                {
                    status = encoder.Compress(chunk.Span, dest.Span, out var bytesConsumed, out var bytesWritten, isFinalBlock: false); // not guarantees finalBlock
                    chunk = chunk.Slice(bytesConsumed);
                    dest = dest.Slice(bytesWritten);
                    writtenInDest += bytesWritten;

                    if (status == OperationStatus.InvalidData)
                    {
                        throw new ZstandardException("ZstandardEncoder returns InvalidData.");
                    }

                    if (dest.Length == 0)
                    {
                        destination.Advance(writtenInDest);
                        await destination.FlushAsync(cancellationToken);

                        writtenInDest = 0;
                        dest = destination.GetMemory(sizeHint);
                    }
                }
            }
            source.AdvanceTo(buffer.End);
        }

        if (writtenInDest != 0)
        {
            destination.Advance(writtenInDest);
            await destination.FlushAsync(cancellationToken);
        }

        // write final block
        {
            var status = OperationStatus.DestinationTooSmall;
            while (status != OperationStatus.Done)
            {
                dest = destination.GetMemory(sizeHint);
                status = encoder.Close(dest.Span, out var bytesWritten);

                if (status == OperationStatus.InvalidData)
                {
                    throw new ZstandardException("ZstandardEncoder.Close returns InvalidData.");
                }

                destination.Advance(bytesWritten);
                await destination.FlushAsync(cancellationToken);
            }
        }
    }

    public static async ValueTask CompressAsync(string sourceFilePath, PipeWriter destination, ZstandardCompressionOptions? options = null, int? maxDegreeOfParallelism = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options, maxDegreeOfParallelism);
        await CompressAsync(sourceFilePath, destination, encoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask CompressAsync(string sourceFilePath, PipeWriter destination, ZstandardEncoder encoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var sourceHandle = File.OpenHandle(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        await CompressAsync(sourceHandle, destination, encoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask CompressAsync(string sourceFilePath, string destinationFilePath, ZstandardCompressionOptions? options = null, int? maxDegreeOfParallelism = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options, maxDegreeOfParallelism);
        await CompressAsync(sourceFilePath, destinationFilePath, encoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask CompressAsync(string sourceFilePath, string destinationFilePath, ZstandardEncoder encoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var sourceHandle = File.OpenHandle(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        using var destinationStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.Asynchronous);
        var destinationWriter = PipeWriter.Create(destinationStream);
        await CompressAsync(sourceHandle, destinationWriter, encoder, requestBufferSize, cancellationToken);
    }

    static int GetBufferSize(int sourceLength, int minimumBufferSize)
    {
        if (sourceLength >= minimumBufferSize) return minimumBufferSize;
        var maxCompressedLength = GetMaxCompressedLength(sourceLength);

        // use smaller for buffer.
        return Math.Min(minimumBufferSize, maxCompressedLength);
    }

    static int GetBufferSize(long sourceLength, int minimumBufferSize)
    {
        if (sourceLength >= minimumBufferSize) return minimumBufferSize;
        var maxCompressedLength = GetMaxCompressedLength((int)sourceLength);

        // use smaller for buffer.
        return Math.Min(minimumBufferSize, maxCompressedLength);
    }

    static ZstandardEncoder CreateEncoder(ZstandardCompressionOptions? options, int? maxDegreeOfParallelism)
    {
        var isSingleThread = maxDegreeOfParallelism is null or 1;
        if (options == null && isSingleThread)
        {
            // use default settings(single-thread)
            return new ZstandardEncoder();
        }
        else
        {
            if (isSingleThread)
            {
                // single-thread
                return new ZstandardEncoder(options ?? ZstandardCompressionOptions.Default);
            }
            else
            {
                var compressionOptions = (options ?? ZstandardCompressionOptions.Default) with
                {
                    NbWorkers = maxDegreeOfParallelism!.Value // is not null
                };
                return new ZstandardEncoder(compressionOptions);
            }
        }
    }
}
