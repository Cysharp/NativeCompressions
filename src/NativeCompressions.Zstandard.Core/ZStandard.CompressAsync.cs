using Microsoft.Win32.SafeHandles;
using NativeCompressions.Internal;
using System.Buffers;
using System.IO.Pipelines;

namespace NativeCompressions;

public static partial class Zstandard
{
    const int MinimumBufferSize = 65536;
    static readonly StreamPipeReaderOptions LeaveOpenPipeReaderOptions = new StreamPipeReaderOptions(leaveOpen: true);

    public static async ValueTask CompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZstandardCompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options);
        await CompressAsync(source, destination, encoder, cancellationToken);
    }

    public static async ValueTask CompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZstandardEncoder encoder, CancellationToken cancellationToken = default)
    {
        var sizeHint = GetBufferSize(source.Length, MinimumBufferSize);

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

    public static async ValueTask CompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, ZstandardCompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options);
        await CompressAsync(source, destination, encoder, cancellationToken);
    }

    public static async ValueTask CompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, ZstandardEncoder encoder, CancellationToken cancellationToken = default)
    {
        var sizeHint = GetBufferSize(source.Length, MinimumBufferSize);
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

    public static ValueTask CompressAsync(SafeFileHandle source, PipeWriter destination, ZstandardCompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return CompressAsync(source, 0, destination, options, cancellationToken);
    }

    public static ValueTask CompressAsync(SafeFileHandle source, PipeWriter destination, ZstandardEncoder encoder, CancellationToken cancellationToken = default)
    {
        return CompressAsync(source, 0, destination, encoder, cancellationToken);
    }

    public static async ValueTask CompressAsync(SafeFileHandle source, long offset, PipeWriter destination, ZstandardCompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options);
        await CompressAsync(source, offset, destination, encoder, cancellationToken);
    }

    public static async ValueTask CompressAsync(SafeFileHandle source, long offset, PipeWriter destination, ZstandardEncoder encoder, CancellationToken cancellationToken = default)
    {
#if NETSTANDARD
        var fs = NonOwningFileStream.Open(source, offset); // not disposed, it does not own the handle
        await CompressAsync(fs, destination, encoder, cancellationToken);
#else
        var sourceLength = RandomAccess.GetLength(source);
        var sizeHint = GetBufferSize(sourceLength, MinimumBufferSize);

        var sourceBuffer = ArrayPool<byte>.Shared.Rent(sizeHint);
        try
        {
            var writtenInDest = 0;
            var dest = destination.GetMemory(sizeHint);
            var remaining = sourceLength - offset;
            while (remaining > 0)
            {
                var currentOffset = sourceLength - remaining; // remaining already accounts for offset
                var read = await RandomAccess.ReadAsync(source, sourceBuffer, currentOffset, cancellationToken);
                if (read == 0) break; // EOF, the file shrank while reading
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

    public static async ValueTask CompressAsync(Stream source, PipeWriter destination, ZstandardCompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options);
        await CompressAsync(source, destination, encoder, cancellationToken);
    }

    public static async ValueTask CompressAsync(Stream source, PipeWriter destination, ZstandardEncoder encoder, CancellationToken cancellationToken = default)
    {
        if (source is MemoryStream ms && ms.TryGetBuffer(out var buffer))
        {
            // honor the stream position, and leave the stream at the end like a normal read would.
            // A position at or past the end is a legal EOF and is left where it is.
            if (ms.Position >= ms.Length)
            {
                await CompressAsync(ReadOnlyMemory<byte>.Empty, destination, encoder, cancellationToken);
                return;
            }
            var position = (int)ms.Position;
            await CompressAsync(((ReadOnlyMemory<byte>)buffer).Slice(position), destination, encoder, cancellationToken);
            ms.Position = ms.Length;
            return;
        }

#if !NETSTANDARD
        if (source is FileStream fs && fs.CanSeek)
        {
            await CompressAsync(fs.SafeFileHandle, fs.Position, destination, encoder, cancellationToken);
            return;
        }
#endif

        var pipeReader = PipeReader.Create(source, LeaveOpenPipeReaderOptions);
        await CompressAsync(pipeReader, destination, encoder, cancellationToken);
        await pipeReader.CompleteAsync();
    }

    public static async ValueTask CompressAsync(PipeReader source, PipeWriter destination, ZstandardCompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options);
        await CompressAsync(source, destination, encoder, cancellationToken);
    }

    public static async ValueTask CompressAsync(PipeReader source, PipeWriter destination, ZstandardEncoder encoder, CancellationToken cancellationToken = default)
    {
        var sizeHint = MinimumBufferSize;

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

    public static async ValueTask CompressAsync(string sourceFilePath, PipeWriter destination, ZstandardCompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options);
        await CompressAsync(sourceFilePath, destination, encoder, cancellationToken);
    }

    public static async ValueTask CompressAsync(string sourceFilePath, PipeWriter destination, ZstandardEncoder encoder, CancellationToken cancellationToken = default)
    {
        using var sourceHandle = File.OpenHandle(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        await CompressAsync(sourceHandle, destination, encoder, cancellationToken);
    }

    public static async ValueTask CompressAsync(string sourceFilePath, string destinationFilePath, ZstandardCompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options);
        await CompressAsync(sourceFilePath, destinationFilePath, encoder, cancellationToken);
    }

    public static async ValueTask CompressAsync(string sourceFilePath, string destinationFilePath, ZstandardEncoder encoder, CancellationToken cancellationToken = default)
    {
        using var sourceHandle = File.OpenHandle(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        using var destinationStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.Asynchronous);
        var destinationWriter = PipeWriter.Create(destinationStream);
        await CompressAsync(sourceHandle, destinationWriter, encoder, cancellationToken);
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

    // Parallel compression is configured through ZstandardCompressionOptions.NbWorkers.
    static ZstandardEncoder CreateEncoder(ZstandardCompressionOptions? options)
    {
        return options == null ? new ZstandardEncoder() : new ZstandardEncoder(options.Value);
    }
}
