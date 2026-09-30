using NativeCompressions.Internal;
using System.Buffers;
using System.IO.Pipelines;

namespace NativeCompressions;

public static partial class Zstandard
{
    const int MinimumBufferSize = 65536;
    // Streams are read in pieces of the size the encoder and decoder work in.
    static readonly StreamPipeReaderOptions LeaveOpenPipeReaderOptions = new StreamPipeReaderOptions(bufferSize: MinimumBufferSize, leaveOpen: true);

    public static async ValueTask CompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZstandardCompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options);
        await CompressAsync(source, destination, encoder, cancellationToken);
    }

    public static async ValueTask CompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZstandardEncoder encoder, CancellationToken cancellationToken = default)
    {
        // checked before anything is compressed, output already handed to the destination cannot be taken back
        cancellationToken.ThrowIfCancellationRequested();
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
            await destination.FlushAndCheckAsync(cancellationToken);
        }
    }

    public static async ValueTask CompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, ZstandardCompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options);
        await CompressAsync(source, destination, encoder, cancellationToken);
    }

    public static async ValueTask CompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, ZstandardEncoder encoder, CancellationToken cancellationToken = default)
    {
        // checked before anything is compressed, output already handed to the destination cannot be taken back
        cancellationToken.ThrowIfCancellationRequested();
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
                    await destination.FlushAndCheckAsync(cancellationToken);

                    writtenInDest = 0;
                    dest = destination.GetSpan(sizeHint);
                }
            }
        }

        if (writtenInDest != 0)
        {
            destination.Advance(writtenInDest);
            await destination.FlushAndCheckAsync(cancellationToken);
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
                await destination.FlushAndCheckAsync(cancellationToken);
            }
        }
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

        var pipeReader = PipeReader.Create(source, LeaveOpenPipeReaderOptions);
        try
        {
            await CompressAsync(pipeReader, destination, encoder, cancellationToken);
        }
        finally
        {
            await pipeReader.CompleteAsync(); // returns the buffers of the reader, also after a failure
        }
    }

    public static async ValueTask CompressAsync(PipeReader source, PipeWriter destination, ZstandardCompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options);
        await CompressAsync(source, destination, encoder, cancellationToken);
    }

    public static async ValueTask CompressAsync(PipeReader source, PipeWriter destination, ZstandardEncoder encoder, CancellationToken cancellationToken = default)
    {
        // checked before anything is compressed, output already handed to the destination cannot be taken back
        cancellationToken.ThrowIfCancellationRequested();
        var sizeHint = MinimumBufferSize;

        var writtenInDest = 0;
        var dest = destination.GetMemory(sizeHint);

        ReadResult result = default;
        while (!result.IsCompleted)
        {
            result = await source.ReadAsync(cancellationToken);
            if (result.IsCanceled) throw new OperationCanceledException();

            // The reader is advanced by what the encoder took, also when the destination fails on the way.
            // Otherwise the reader would stay in the middle of a read and refuse the next one.
            var buffer = result.Buffer;
            long consumed = 0;
            try
            {
                foreach (var item in buffer)
                {
                    var chunk = item;
                    var status = OperationStatus.DestinationTooSmall;
                    while (status != OperationStatus.Done) // when chunk is fully consumed, go to next chunk
                    {
                        status = encoder.Compress(chunk.Span, dest.Span, out var bytesConsumed, out var bytesWritten, isFinalBlock: false); // not guarantees finalBlock
                        chunk = chunk.Slice(bytesConsumed);
                        consumed += bytesConsumed;
                        dest = dest.Slice(bytesWritten);
                        writtenInDest += bytesWritten;

                        if (status == OperationStatus.InvalidData)
                        {
                            throw new ZstandardException("ZstandardEncoder returns InvalidData.");
                        }

                        if (dest.Length == 0)
                        {
                            destination.Advance(writtenInDest);
                            await destination.FlushAndCheckAsync(cancellationToken);

                            writtenInDest = 0;
                            dest = destination.GetMemory(sizeHint);
                        }
                    }
                }
            }
            finally
            {
                source.AdvanceTo(buffer.GetPosition(consumed)); // examined only up to there, so what is left is readable right away
            }

            // Everything compressed so far goes out before more input is awaited. The other end may be
            // waiting for it before it sends that input.
            if (writtenInDest != 0)
            {
                destination.Advance(writtenInDest);
                await destination.FlushAndCheckAsync(cancellationToken);

                writtenInDest = 0;
                dest = destination.GetMemory(sizeHint);
            }
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
                await destination.FlushAndCheckAsync(cancellationToken);
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
        using var source = OpenSource(sourceFilePath);
        await CompressAsync(source, destination, encoder, cancellationToken);
    }

    public static async ValueTask CompressAsync(string sourceFilePath, string destinationFilePath, ZstandardCompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var encoder = CreateEncoder(options);
        await CompressAsync(sourceFilePath, destinationFilePath, encoder, cancellationToken);
    }

    public static async ValueTask CompressAsync(string sourceFilePath, string destinationFilePath, ZstandardEncoder encoder, CancellationToken cancellationToken = default)
    {
        using var source = OpenSource(sourceFilePath);
        using var destinationStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.Asynchronous);
        var destinationWriter = PipeWriter.Create(destinationStream);
        try
        {
            await CompressAsync(source, destinationWriter, encoder, cancellationToken);
        }
        finally
        {
            await destinationWriter.CompleteAsync(); // returns the buffers of the writer
        }
    }

    // The PipeReader reads in large pieces, so the FileStream needs no buffer of its own.
    static FileStream OpenSource(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous);

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

    static ZstandardEncoder CreateEncoder(ZstandardCompressionOptions? options)
    {
        return options == null ? new ZstandardEncoder() : new ZstandardEncoder(options.Value);
    }
}
