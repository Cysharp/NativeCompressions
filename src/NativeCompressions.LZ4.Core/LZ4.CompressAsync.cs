using Microsoft.Win32.SafeHandles;
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

    static readonly StreamPipeReaderOptions LeaveOpenPipeReaderOptions = new StreamPipeReaderOptions(leaveOpen: true);

    public static async ValueTask CompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, LZ4CompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var reader = PipeReader.Create(new ReadOnlySequence<byte>(source));
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
        var reader = PipeReader.Create(source);
        try
        {
            await CompressCoreAsync(reader, source.Length, destination, options, cancellationToken);
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    public static ValueTask CompressAsync(SafeFileHandle source, PipeWriter destination, LZ4CompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return CompressAsync(source, 0, destination, options, cancellationToken);
    }

    public static async ValueTask CompressAsync(SafeFileHandle source, long offset, PipeWriter destination, LZ4CompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (source == null || source.IsInvalid || source.IsClosed)
        {
            throw new ArgumentException("Invalid file handle", nameof(source));
        }
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

#if NETSTANDARD
        // the handle is read as a stream of unknown length
        var stream = NonOwningFileStream.Open(source, offset); // not disposed, it does not own the handle
        long? length = null;
#else
        var stream = new RandomAccessReadStream(source, offset);
        long? length = Math.Max(0, RandomAccess.GetLength(source) - offset); // an offset at or past the end is an empty source
#endif
        var reader = PipeReader.Create(stream, LargeBufferLeaveOpenPipeReaderOptions);
        try
        {
            await CompressCoreAsync(reader, length, destination, options, cancellationToken);
        }
        finally
        {
            await reader.CompleteAsync(); // returns the buffers of the reader, also after a failure
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

#if !NETSTANDARD
        if (source is FileStream fs && fs.CanSeek)
        {
            // A position at or past the end is a legal EOF and is left where it is, the same as for MemoryStream.
            if (fs.Position >= fs.Length)
            {
                await CompressAsync(ReadOnlyMemory<byte>.Empty, destination, options, cancellationToken);
                return;
            }

            await CompressAsync(fs.SafeFileHandle, fs.Position, destination, options, cancellationToken);
            fs.Position = fs.Length; // the handle was read directly, leave the stream at the end like a normal read would
            return;
        }
#endif

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
        using var sourceHandle = File.OpenHandle(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        using var destinationStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.Asynchronous);
        var destinationWriter = PipeWriter.Create(destinationStream);
        try
        {
            await CompressAsync(sourceHandle, destinationWriter, options, cancellationToken);
        }
        finally
        {
            await destinationWriter.CompleteAsync(); // returns the buffers of the writer
        }
    }

    public static async ValueTask CompressAsync(string sourceFilePath, PipeWriter destination, LZ4CompressionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var sourceHandle = File.OpenHandle(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        await CompressAsync(sourceHandle, destination, options, cancellationToken);
    }

    // ---- core

    // Compresses the whole input as one frame. A recorded length is verified when the frame closes,
    // the frame fails to close when the input turns out to be a different length.
    static async ValueTask CompressCoreAsync(PipeReader source, long? knownLength, PipeWriter destination, LZ4CompressionOptions? givenOptions, CancellationToken cancellationToken)
    {
        var options = givenOptions ?? LZ4CompressionOptions.Default;
        if (knownLength != null)
        {
            options.ThrowIfContentSizeDiffers(knownLength.Value);
            options = options with
            {
                ContentSize = givenOptions == null ? (ulong)knownLength.Value : options.ContentSize,
                BlockSizeID = options.BlockSizeID == BlockSizeId.Default ? DetermineBlockSize(knownLength.Value) : options.BlockSizeID,
            };
        }

        var blockSize = GetMaxBlockSize(options.BlockSizeID);
        using var encoder = new LZ4Encoder(options);

        ReadResult result = default;
        while (!result.IsCompleted)
        {
            result = await source.ReadAsync(cancellationToken);
            if (result.IsCanceled) throw new OperationCanceledException();

            foreach (var segment in result.Buffer)
            {
                var src = segment;
                while (!src.IsEmpty)
                {
                    // one block at a time, so the destination never needs more than a block's worth of room
                    var count = Math.Min(src.Length, blockSize);
                    var buffer = destination.GetSpan(encoder.GetMaxCompressedLength(count, includingHeader: true, includingFooter: false));

                    var written = encoder.Compress(src.Span.Slice(0, count), buffer);
                    if (written > 0) // nothing is written while the encoder buffers a block
                    {
                        destination.Advance(written);
                        await destination.FlushAndCheckAsync(cancellationToken);
                    }
                    src = src.Slice(count);
                }
            }
            source.AdvanceTo(result.Buffer.End);
        }

        // what the encoder still buffers, the footer, and the header when nothing was written at all
        var lastBuffer = destination.GetSpan(encoder.GetMaxCompressedLength(0));
        var lastWritten = encoder.Close(lastBuffer);
        destination.Advance(lastWritten);
        await destination.FlushAndCheckAsync(cancellationToken);
    }

    static int GetMaxBlockSize(BlockSizeId id)
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
