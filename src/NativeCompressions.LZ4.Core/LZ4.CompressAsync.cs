using NativeCompressions.Internal;
using System.Buffers;
using System.IO.Pipelines;

namespace NativeCompressions;

public static partial class LZ4
{
    // Every CompressAsync overload turns its source into a PipeReader and runs CompressCoreAsync, so they share
    // one behavior. Sources with a known length (memory, sequence, file) get a block size chosen for that length.
    // Without options the known length is recorded in the frame header. With options, ContentSize is what the
    // caller asked for: it must match a known length, and for a stream or pipe it is checked when the frame closes.

    // Streams are read in 64KB pieces, the size the encoder and decoder work in.
    static readonly StreamPipeReaderOptions LeaveOpenPipeReaderOptions = new StreamPipeReaderOptions(bufferSize: 65536, leaveOpen: true);

    public static async ValueTask CompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, LZ4CompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        // An empty source goes in as the empty sequence. The reader answers a read of an empty sequence with that one,
        // and a position taken from it does not belong to a slice that starts elsewhere.
        var reader = PipeReader.Create(source.IsEmpty ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(source));
        try
        {
            await CompressCoreAsync(reader, source.Length, destination, options, cancellationToken);
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    public static async ValueTask CompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, LZ4CompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var reader = PipeReader.Create(source.IsEmpty ? ReadOnlySequence<byte>.Empty : source); // see the ReadOnlyMemory overload
        try
        {
            await CompressCoreAsync(reader, source.Length, destination, options, cancellationToken);
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    public static async ValueTask CompressAsync(Stream source, PipeWriter destination, LZ4CompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (source is MemoryStream ms && ms.TryGetBuffer(out var buffer))
        {
            // honor the stream position, and leave the stream at the end like a normal read would.
            // A position at or past the end is a legal EOF and is left where it is.
            if (ms.Position >= ms.Length)
            {
                await CompressAsync(ReadOnlyMemory<byte>.Empty, destination, options, cancellationToken);
                return;
            }
            var position = (int)ms.Position;
            await CompressAsync(((ReadOnlyMemory<byte>)buffer).Slice(position), destination, options, cancellationToken);
            ms.Position = ms.Length;
            return;
        }

        // any other seekable stream (a MemoryStream without an exposable buffer, for example) still knows its length
        long? length = source.CanSeek ? Math.Max(0, source.Length - source.Position) : null;

        var pipeReader = PipeReader.Create(source, LeaveOpenPipeReaderOptions);
        try
        {
            await CompressCoreAsync(pipeReader, length, destination, options, cancellationToken);
        }
        finally
        {
            await pipeReader.CompleteAsync(); // returns the buffers of the reader, also after a failure
        }
    }

    public static ValueTask CompressAsync(PipeReader source, PipeWriter destination, LZ4CompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return CompressCoreAsync(source, null, destination, options, cancellationToken);
    }

    public static async ValueTask CompressAsync(string sourceFilePath, string destinationFilePath, LZ4CompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        // checked before any file is opened, a cancelled call must not truncate the destination
        cancellationToken.ThrowIfCancellationRequested();
        using var source = OpenSource(sourceFilePath);
        using var destinationStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.Asynchronous);
        var destinationWriter = PipeWriter.Create(destinationStream);
        try
        {
            await CompressAsync(source, destinationWriter, options, cancellationToken);
        }
        finally
        {
            await destinationWriter.CompleteAsync(); // returns the buffers of the writer
        }
    }

    public static async ValueTask CompressAsync(string sourceFilePath, PipeWriter destination, LZ4CompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var source = OpenSource(sourceFilePath);
        await CompressAsync(source, destination, options, cancellationToken);
    }

    // The PipeReader reads in large pieces, so the FileStream needs no buffer of its own.
    static FileStream OpenSource(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous);

    // ---- core

    // Compresses the whole input as one frame. A recorded length is verified when the frame closes,
    // the frame fails to close when the input turns out to be a different length.
    static async ValueTask CompressCoreAsync(PipeReader source, long? knownLength, PipeWriter destination, LZ4CompressionOptions? givenOptions, CancellationToken cancellationToken)
    {
        // checked before anything is compressed, output already handed to the destination cannot be taken back
        cancellationToken.ThrowIfCancellationRequested();
        var options = givenOptions ?? LZ4CompressionOptions.Default;
        if (knownLength != null)
        {
            options.ThrowIfContentSizeDiffers(knownLength.Value);
            options = options with
            {
                ContentSize = givenOptions == null ? (ulong)knownLength.Value : options.ContentSize,
                BlockSizeId = options.BlockSizeId == BlockSizeId.Default ? DetermineBlockSize(knownLength.Value) : options.BlockSizeId,
            };
        }

        var blockSize = GetMaxBlockSize(options.BlockSizeId);
        using var encoder = new LZ4Encoder(options);

        ReadResult result = default;
        while (!result.IsCompleted)
        {
            result = await source.ReadAsync(cancellationToken);
            if (result.IsCanceled) throw new OperationCanceledException();

            // The reader is advanced by what the encoder took, also when the destination fails on the way.
            // Otherwise the reader would stay in the middle of a read and refuse the next one.
            var input = result.Buffer;
            long consumed = 0;
            try
            {
                foreach (var segment in input)
                {
                    var src = segment;
                    while (!src.IsEmpty)
                    {
                        // one block at a time, so the destination never needs more than a block's worth of room
                        var count = Math.Min(src.Length, blockSize);
                        var buffer = destination.GetSpan(encoder.GetMaxCompressedLength(count, includingHeader: true, includingFooter: false));

                        // the buffer takes the whole count in one call, a smaller part is taken again on the next turn
                        var status = encoder.Compress(src.Span.Slice(0, count), buffer, out var taken, out var written, isFinalBlock: false);
                        if (status == OperationStatus.InvalidData) throw new LZ4Exception("LZ4 compression failed.");
                        consumed += taken;
                        if (written > 0) // nothing is written while the encoder buffers a block
                        {
                            destination.Advance(written);
                            await destination.FlushAndCheckAsync(cancellationToken);
                        }
                        src = src.Slice(taken);
                    }
                }
            }
            finally
            {
                source.AdvanceTo(input.GetPosition(consumed)); // examined only up to there, so what is left is readable right away
            }
        }

        // what the encoder still buffers, the footer, and the header when nothing was written at all
        var closing = OperationStatus.DestinationTooSmall;
        while (closing == OperationStatus.DestinationTooSmall)
        {
            var lastBuffer = destination.GetSpan(encoder.GetMaxFlushBufferLength());
            closing = encoder.Close(lastBuffer, out var lastWritten);
            destination.Advance(lastWritten);
            await destination.FlushAndCheckAsync(cancellationToken);
        }
        if (closing != OperationStatus.Done)
        {
            throw new LZ4Exception("The LZ4 frame could not be closed, for example the source length differs from ContentSize.");
        }
    }

    internal static int GetMaxBlockSize(BlockSizeId id)
    {
        switch (id)
        {
            case BlockSizeId.Default:
            case BlockSizeId.Max64KB:
                return 64 * 1024;
            case BlockSizeId.Max256KB:
                return 256 * 1024;
            case BlockSizeId.Max1MB:
                return 1024 * 1024;
            case BlockSizeId.Max4MB:
                return 4 * 1024 * 1024;
            default:
                throw new LZ4Exception("Invalid blockSize");
        }
    }

    static BlockSizeId DetermineBlockSize(long sourceLength)
    {
        return sourceLength switch
        {
            < 1 * 1024 * 1024 => BlockSizeId.Max64KB,     // < 1MB
            < 10 * 1024 * 1024 => BlockSizeId.Max256KB,   // < 10MB
            < 100 * 1024 * 1024 => BlockSizeId.Max1MB,    // < 100MB
            _ => BlockSizeId.Max4MB
        };
    }
}
