using NativeCompressions.Internal;
using System.Buffers;
using System.IO.Pipelines;

namespace NativeCompressions;

public static partial class Zstandard
{
    // All DecompressAsync overloads feed their input through FeedAsync and end with FinishAsync,
    // so they share one behavior: concatenated frames are all decoded (same as ZstandardStream),
    // invalid data throws, and input that ends inside a frame throws.

    public static async ValueTask DecompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZstandardDecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(source, destination, decoder, cancellationToken);
    }

    public static async ValueTask DecompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZstandardDecoder decoder, CancellationToken cancellationToken = default)
    {
        // checked before anything is decoded, output already handed to the destination cannot be taken back
        cancellationToken.ThrowIfCancellationRequested();
        var status = await FeedAsync(decoder, source, destination, MinimumBufferSize, cancellationToken);
        await FinishAsync(decoder, status, source.Length > 0, destination, MinimumBufferSize, cancellationToken);
    }

    public static async ValueTask DecompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, ZstandardDecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(source, destination, decoder, cancellationToken);
    }

    public static async ValueTask DecompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, ZstandardDecoder decoder, CancellationToken cancellationToken = default)
    {
        // checked before anything is decoded, output already handed to the destination cannot be taken back
        cancellationToken.ThrowIfCancellationRequested();
        var status = OperationStatus.NeedMoreData;
        var anyInput = false;
        foreach (var segment in source)
        {
            if (segment.IsEmpty) continue;
            anyInput = true;
            status = await FeedAsync(decoder, segment, destination, MinimumBufferSize, cancellationToken);
        }

        await FinishAsync(decoder, status, anyInput, destination, MinimumBufferSize, cancellationToken);
    }

    public static async ValueTask DecompressAsync(Stream source, PipeWriter destination, ZstandardDecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(source, destination, decoder, cancellationToken);
    }

    public static async ValueTask DecompressAsync(Stream source, PipeWriter destination, ZstandardDecoder decoder, CancellationToken cancellationToken = default)
    {
        if (source is MemoryStream ms && ms.TryGetBuffer(out var buffer))
        {
            // honor the stream position, and leave the stream at the end like a normal read would.
            // A position at or past the end is a legal EOF and is left where it is.
            if (ms.Position >= ms.Length)
            {
                await DecompressAsync(ReadOnlyMemory<byte>.Empty, destination, decoder, cancellationToken);
                return;
            }
            var position = (int)ms.Position;
            await DecompressAsync(((ReadOnlyMemory<byte>)buffer).Slice(position), destination, decoder, cancellationToken);
            ms.Position = ms.Length;
            return;
        }

        var pipeReader = PipeReader.Create(source, LeaveOpenPipeReaderOptions);
        try
        {
            await DecompressAsync(pipeReader, destination, decoder, cancellationToken);
        }
        finally
        {
            await pipeReader.CompleteAsync(); // returns the buffers of the reader, also after a failure
        }
    }

    public static async ValueTask DecompressAsync(PipeReader source, PipeWriter destination, ZstandardDecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(source, destination, decoder, cancellationToken);
    }

    public static async ValueTask DecompressAsync(PipeReader source, PipeWriter destination, ZstandardDecoder decoder, CancellationToken cancellationToken = default)
    {
        // checked before anything is decoded, output already handed to the destination cannot be taken back
        cancellationToken.ThrowIfCancellationRequested();
        var status = OperationStatus.NeedMoreData;
        var anyInput = false;
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
                    anyInput = true;
                    status = await FeedAsync(decoder, segment, destination, MinimumBufferSize, cancellationToken, progress);
                }
            }
            finally
            {
                source.AdvanceTo(buffer.GetPosition(progress.Consumed)); // examined only up to there, so what is left is readable right away
            }
        }

        await FinishAsync(decoder, status, anyInput, destination, MinimumBufferSize, cancellationToken);
    }

    public static async ValueTask DecompressAsync(string sourceFilePath, PipeWriter destination, ZstandardDecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(sourceFilePath, destination, decoder, cancellationToken);
    }

    public static async ValueTask DecompressAsync(string sourceFilePath, PipeWriter destination, ZstandardDecoder decoder, CancellationToken cancellationToken = default)
    {
        using var source = OpenSource(sourceFilePath);
        await DecompressAsync(source, destination, decoder, cancellationToken);
    }

    public static async ValueTask DecompressAsync(string sourceFilePath, string destinationFilePath, ZstandardDecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(sourceFilePath, destinationFilePath, decoder, cancellationToken);
    }

    public static async ValueTask DecompressAsync(string sourceFilePath, string destinationFilePath, ZstandardDecoder decoder, CancellationToken cancellationToken = default)
    {
        // checked before any file is opened, a cancelled call must not truncate the destination
        cancellationToken.ThrowIfCancellationRequested();
        using var source = OpenSource(sourceFilePath);
        using var destinationStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.Asynchronous);
        var destinationWriter = PipeWriter.Create(destinationStream);
        try
        {
            await DecompressAsync(source, destinationWriter, decoder, cancellationToken);
        }
        finally
        {
            await destinationWriter.CompleteAsync(); // returns the buffers of the writer
        }
    }

    // Feeds one chunk of compressed input and writes whatever it decodes to destination.
    // A frame may end and the next one start anywhere inside the chunk.
    // progress, when given, counts the bytes of chunk the decoder took, also when this method fails
    static async ValueTask<OperationStatus> FeedAsync(ZstandardDecoder decoder, ReadOnlyMemory<byte> chunk, PipeWriter destination, int sizeHint, CancellationToken cancellationToken, FeedProgress? progress = null)
    {
        var status = OperationStatus.NeedMoreData;
        var pending = 0; // bytes advanced but not yet flushed

        // DestinationTooSmall means the decoder still holds output, which is taken out with empty input.
        // Leaving it there would hold it back until more input arrives.
        while (chunk.Length > 0 || status == OperationStatus.DestinationTooSmall)
        {
            var dest = destination.GetMemory(sizeHint);
            status = decoder.Decompress(chunk.Span, dest.Span, out var bytesConsumed, out var bytesWritten);
            chunk = chunk.Slice(bytesConsumed);
            if (progress != null) progress.Consumed += bytesConsumed;
            destination.Advance(bytesWritten);
            pending += bytesWritten;

            if (bytesConsumed == 0 && bytesWritten == 0 && status == OperationStatus.DestinationTooSmall)
            {
                throw new ZstandardException("Zstandard decoder made no progress.");
            }

            switch (status)
            {
                case OperationStatus.InvalidData:
                    throw new ZstandardException("Zstandard decoder returns InvalidData.");

                case OperationStatus.Done:
                    // frame finished, another frame may follow in the remaining input
                    decoder.Reset();
                    break;
            }

            if (pending >= sizeHint)
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

    // Drains output still buffered in the native context and validates that the input ended on a frame boundary.
    static async ValueTask FinishAsync(ZstandardDecoder decoder, OperationStatus status, bool anyInput, PipeWriter destination, int sizeHint, CancellationToken cancellationToken)
    {
        while (status == OperationStatus.DestinationTooSmall)
        {
            var dest = destination.GetMemory(sizeHint);
            status = decoder.Decompress(ReadOnlySpan<byte>.Empty, dest.Span, out _, out var bytesWritten);
            destination.Advance(bytesWritten);
            await destination.FlushAndCheckAsync(cancellationToken);

            if (status == OperationStatus.Done)
            {
                decoder.Reset();
            }
        }

        // a decoder handed in by the caller may already be inside a frame, so no input is not enough to call it a clean end
        if (decoder.IsFrameInProgress)
        {
            throw new ZstandardException("Invalid Zstandard frame: input ends inside a frame.");
        }

        if (!anyInput)
        {
            return; // no frames at all, nothing to write
        }

        if (status != OperationStatus.Done)
        {
            throw new ZstandardException($"Zstandard decoder returns {status}.");
        }
    }

    // How much of the input of one read the decoder took so far.
    sealed class FeedProgress
    {
        public long Consumed;
    }
}
