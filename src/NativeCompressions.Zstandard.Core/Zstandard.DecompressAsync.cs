using Microsoft.Win32.SafeHandles;
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

    public static ValueTask DecompressAsync(SafeFileHandle source, PipeWriter destination, ZstandardDecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return DecompressAsync(source, 0, destination, options, cancellationToken);
    }

    public static ValueTask DecompressAsync(SafeFileHandle source, PipeWriter destination, ZstandardDecoder decoder, CancellationToken cancellationToken = default)
    {
        return DecompressAsync(source, 0, destination, decoder, cancellationToken);
    }

    public static async ValueTask DecompressAsync(SafeFileHandle source, long offset, PipeWriter destination, ZstandardDecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(source, offset, destination, decoder, cancellationToken);
    }

    public static async ValueTask DecompressAsync(SafeFileHandle source, long offset, PipeWriter destination, ZstandardDecoder decoder, CancellationToken cancellationToken = default)
    {
#if NETSTANDARD2_1
        var fs = NonOwningFileStream.Open(source, offset); // not disposed, it does not own the handle
        await DecompressAsync(fs, destination, decoder, cancellationToken);
#else
        var sourceLength = RandomAccess.GetLength(source);
        var sourceBuffer = ArrayPool<byte>.Shared.Rent(MinimumBufferSize);
        try
        {
            var status = OperationStatus.NeedMoreData;
            var anyInput = false;
            var remaining = sourceLength - offset;
            while (remaining > 0)
            {
                var currentOffset = sourceLength - remaining; // remaining already accounts for offset
                var read = await RandomAccess.ReadAsync(source, sourceBuffer, currentOffset, cancellationToken);
                if (read == 0) break; // EOF, the file shrank while reading

                anyInput = true;
                status = await FeedAsync(decoder, sourceBuffer.AsMemory(0, read), destination, MinimumBufferSize, cancellationToken);
                remaining -= read;
            }

            await FinishAsync(decoder, status, anyInput, destination, MinimumBufferSize, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBuffer, clearArray: false);
        }
#endif
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

#if !NETSTANDARD2_1
        if (source is FileStream fs && fs.CanSeek)
        {
            await DecompressAsync(fs.SafeFileHandle, fs.Position, destination, decoder, cancellationToken);
            return;
        }
#endif

        var pipeReader = PipeReader.Create(source, LeaveOpenPipeReaderOptions);
        await DecompressAsync(pipeReader, destination, decoder, cancellationToken);
        await pipeReader.CompleteAsync();
    }

    public static async ValueTask DecompressAsync(PipeReader source, PipeWriter destination, ZstandardDecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(source, destination, decoder, cancellationToken);
    }

    public static async ValueTask DecompressAsync(PipeReader source, PipeWriter destination, ZstandardDecoder decoder, CancellationToken cancellationToken = default)
    {
        var status = OperationStatus.NeedMoreData;
        var anyInput = false;

        ReadResult result = default;
        while (!result.IsCompleted)
        {
            result = await source.ReadAsync(cancellationToken);
            if (result.IsCanceled) throw new OperationCanceledException();

            var buffer = result.Buffer;
            foreach (var segment in buffer)
            {
                if (segment.IsEmpty) continue;
                anyInput = true;
                status = await FeedAsync(decoder, segment, destination, MinimumBufferSize, cancellationToken);
            }
            source.AdvanceTo(buffer.End);
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
        using var sourceHandle = File.OpenHandle(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        await DecompressAsync(sourceHandle, destination, decoder, cancellationToken);
    }

    public static async ValueTask DecompressAsync(string sourceFilePath, string destinationFilePath, ZstandardDecompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(sourceFilePath, destinationFilePath, decoder, cancellationToken);
    }

    public static async ValueTask DecompressAsync(string sourceFilePath, string destinationFilePath, ZstandardDecoder decoder, CancellationToken cancellationToken = default)
    {
        using var sourceHandle = File.OpenHandle(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        using var destinationStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.Asynchronous);
        var destinationWriter = PipeWriter.Create(destinationStream);
        await DecompressAsync(sourceHandle, destinationWriter, decoder, cancellationToken);
    }

    // Feeds one chunk of compressed input and writes whatever it decodes to destination.
    // A frame may end and the next one start anywhere inside the chunk.
    static async ValueTask<OperationStatus> FeedAsync(ZstandardDecoder decoder, ReadOnlyMemory<byte> chunk, PipeWriter destination, int sizeHint, CancellationToken cancellationToken)
    {
        var status = OperationStatus.NeedMoreData;
        var pending = 0; // bytes advanced but not yet flushed

        while (chunk.Length > 0)
        {
            var dest = destination.GetMemory(sizeHint);
            status = decoder.Decompress(chunk.Span, dest.Span, out var bytesConsumed, out var bytesWritten);
            chunk = chunk.Slice(bytesConsumed);
            destination.Advance(bytesWritten);
            pending += bytesWritten;

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
                await destination.FlushAsync(cancellationToken);
                pending = 0;
            }
        }

        if (pending > 0)
        {
            await destination.FlushAsync(cancellationToken);
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
            await destination.FlushAsync(cancellationToken);

            if (status == OperationStatus.Done)
            {
                decoder.Reset();
            }
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
}
