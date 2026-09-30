using NativeCompressions.Internal;
using System.Buffers;
using System.IO.Pipelines;

namespace NativeCompressions;

public static partial class LZ4
{
    // Every DecompressAsync overload turns its source into a PipeReader and runs DecompressCoreAsync,
    // so they share one behavior: concatenated frames (and skippable frames) are all decoded, the same
    // as LZ4Stream and the one-shot Decompress. Invalid data and input that ends inside a frame throw LZ4Exception.

    const int DecompressOutputSizeHint = 65536;

    public static async ValueTask DecompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, LZ4DecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var reader = PipeReader.Create(new ReadOnlySequence<byte>(source));
        try
        {
            await DecompressCoreAsync(reader, destination, options ?? LZ4DecompressionOptions.Default, cancellationToken);
        }
        finally
        {
            await reader.CompleteAsync(); // returns the buffers of the reader, also after a failure
        }
    }

    public static async ValueTask DecompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, LZ4DecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var reader = PipeReader.Create(source);
        try
        {
            await DecompressCoreAsync(reader, destination, options ?? LZ4DecompressionOptions.Default, cancellationToken);
        }
        finally
        {
            await reader.CompleteAsync(); // returns the buffers of the reader, also after a failure
        }
    }

    public static async ValueTask DecompressAsync(Stream source, PipeWriter destination, LZ4DecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (source is MemoryStream ms && ms.TryGetBuffer(out var buffer))
        {
            // honor the stream position, and leave the stream at the end like a normal read would.
            // A position at or past the end is a legal EOF and is left where it is.
            if (ms.Position >= ms.Length)
            {
                await DecompressAsync(ReadOnlyMemory<byte>.Empty, destination, options, cancellationToken);
                return;
            }
            var position = (int)ms.Position;
            await DecompressAsync(((ReadOnlyMemory<byte>)buffer).Slice(position), destination, options, cancellationToken);
            ms.Position = ms.Length;
            return;
        }

        var reader = PipeReader.Create(source, LeaveOpenPipeReaderOptions);
        try
        {
            await DecompressCoreAsync(reader, destination, options ?? LZ4DecompressionOptions.Default, cancellationToken);
        }
        finally
        {
            await reader.CompleteAsync(); // returns the buffers of the reader, also after a failure
        }
    }

    public static ValueTask DecompressAsync(PipeReader source, PipeWriter destination, LZ4DecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return DecompressCoreAsync(source, destination, options ?? LZ4DecompressionOptions.Default, cancellationToken);
    }

    public static async ValueTask DecompressAsync(string sourceFilePath, string destinationFilePath, LZ4DecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var source = OpenSource(sourceFilePath);
        using var destinationStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.Asynchronous);
        var destinationWriter = PipeWriter.Create(destinationStream);
        try
        {
            await DecompressAsync(source, destinationWriter, options, cancellationToken);
        }
        finally
        {
            await destinationWriter.CompleteAsync(); // returns the buffers of the writer
        }
    }

    public static async ValueTask DecompressAsync(string sourceFilePath, PipeWriter destination, LZ4DecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var source = OpenSource(sourceFilePath);
        await DecompressAsync(source, destination, options, cancellationToken);
    }

    // ---- core

    static async ValueTask DecompressCoreAsync(PipeReader source, PipeWriter destination, LZ4DecompressionOptions options, CancellationToken cancellationToken)
    {
        using var decoder = new LZ4Decoder(options.WithoutStableDst());
        var status = OperationStatus.NeedMoreData;
        var progress = new FeedProgress();

        ReadResult result = default;
        while (!result.IsCompleted)
        {
            result = await source.ReadAsync(cancellationToken);
            if (result.IsCanceled) throw new OperationCanceledException();

            // The reader is advanced by what the decoder took, also when the destination fails on the way.
            // Otherwise the reader would stay in the middle of a read and refuse the next one.
            var buffer = result.Buffer;
            progress.Consumed = 0;
            try
            {
                foreach (var segment in buffer)
                {
                    if (segment.IsEmpty) continue;
                    status = await FeedAsync(decoder, segment, destination, cancellationToken, progress);
                }
            }
            finally
            {
                source.AdvanceTo(buffer.GetPosition(progress.Consumed), buffer.End);
            }
        }

        // input is exhausted, write out what the decoder still holds
        while (status == OperationStatus.DestinationTooSmall)
        {
            var dest = destination.GetMemory(DecompressOutputSizeHint);
            status = decoder.Decompress(ReadOnlySpan<byte>.Empty, dest.Span, out _, out var bytesWritten);
            destination.Advance(bytesWritten);
            await destination.FlushAndCheckAsync(cancellationToken);

            if (bytesWritten == 0 && status == OperationStatus.DestinationTooSmall)
            {
                throw new LZ4Exception("Invalid LZ4 frame: decoder made no progress.");
            }
            if (status == OperationStatus.Done)
            {
                decoder.Reset();
            }
        }

        if (decoder.IsFrameInProgress)
        {
            throw new LZ4Exception("Invalid LZ4 frame: input ends inside a frame.");
        }
    }

    // Feeds one chunk of compressed input and writes whatever it decodes to destination.
    // A frame may end and the next one start anywhere inside the chunk. Everything decoded is flushed before
    // returning, so the caller can wait for more input without holding data back.
    // progress, when given, counts the bytes of chunk the decoder took, also when this method fails
    static async ValueTask<OperationStatus> FeedAsync(LZ4Decoder decoder, ReadOnlyMemory<byte> chunk, PipeWriter destination, CancellationToken cancellationToken, FeedProgress? progress = null)
    {
        var status = OperationStatus.NeedMoreData;
        var pending = 0; // bytes advanced but not yet flushed

        // DestinationTooSmall means the decoder still holds output, which is taken out with empty input
        while (chunk.Length > 0 || status == OperationStatus.DestinationTooSmall)
        {
            var dest = destination.GetMemory(DecompressOutputSizeHint);
            status = decoder.Decompress(chunk.Span, dest.Span, out var bytesConsumed, out var bytesWritten);
            chunk = chunk.Slice(bytesConsumed);
            if (progress != null) progress.Consumed += bytesConsumed;
            destination.Advance(bytesWritten);
            pending += bytesWritten;

            switch (status)
            {
                case OperationStatus.InvalidData:
                    throw new LZ4Exception("Invalid LZ4 frame.");

                case OperationStatus.Done:
                    // frame finished, another frame may follow in the remaining input
                    decoder.Reset();
                    break;

                case OperationStatus.DestinationTooSmall when bytesConsumed == 0 && bytesWritten == 0:
                    throw new LZ4Exception("Invalid LZ4 frame: decoder made no progress.");
            }

            if (pending >= DecompressOutputSizeHint)
            {
                await destination.FlushAndCheckAsync(cancellationToken);
                pending = 0;
            }
        }

        if (pending > 0)
        {
            await destination.FlushAndCheckAsync(cancellationToken);
        }

        return status;
    }

    // How much of the input of one read the decoder took so far.
    sealed class FeedProgress
    {
        public long Consumed;
    }
}
