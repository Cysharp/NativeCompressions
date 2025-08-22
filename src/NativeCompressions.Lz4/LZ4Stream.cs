using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace NativeCompressions.LZ4;

public sealed class LZ4Stream : Stream
{
    const int DecoderBufferSize = 65536;

    LZ4Encoder encoder;
    LZ4Decoder decoder;

    Stream stream;
    CompressionMode mode;
    bool leaveOpen;
    bool isDisposed;

    byte[]? buffer; // both compress and decompress
    int readBufferOffset; // for decompress
    int readBufferCount; // for decompress

    public LZ4Stream(Stream stream, CompressionMode mode, bool leaveOpen = false)
    {
        this.mode = mode;
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.readBufferCount = 0;

        if (mode == CompressionMode.Decompress)
        {
            this.decoder = new LZ4Decoder();
        }
        else
        {
            this.encoder = new LZ4Encoder(null, null);
        }
    }

    public LZ4Stream(Stream stream, LZ4FrameOptions? options, LZ4CompressionDictionary? compressionDictionary, bool leaveOpen = false)
    {
        this.mode = CompressionMode.Compress;
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.readBufferCount = 0;
        this.encoder = new LZ4Encoder(options, compressionDictionary);
    }

    public override bool CanRead => mode == CompressionMode.Decompress && stream != null && stream.CanRead;
    public override bool CanWrite => mode == CompressionMode.Compress && stream != null && stream.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    #region Encode

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteCore(new ReadOnlySpan<byte>(buffer, offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        WriteCore(buffer);
    }

    public override void WriteByte(byte value)
    {
        Span<byte> span = [value];
        WriteCore(span);
    }

    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        return TaskToAsyncResultShim.Begin(WriteAsync(buffer, offset, count, CancellationToken.None), callback, state);
    }

    public override void EndWrite(IAsyncResult asyncResult)
    {
        TaskToAsyncResultShim.End(asyncResult);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled(cancellationToken)
            : WriteCoreAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
        if (mode != CompressionMode.Compress)
        {
            throw new InvalidOperationException("Write operation must be Compress mode.");
        }

        if (buffer == null) return;

        // Write acquire max GetMaxCompressedLength per source so buffer size is safe to call Flush
        var written = encoder.Flush(buffer);
        stream.Write(buffer, 0, written);
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (mode != CompressionMode.Compress)
        {
            throw new InvalidOperationException("Write operation must be Compress mode.");
        }
        if (buffer == null) return;

        // Write acquire max GetMaxCompressedLength per source so buffer size is safe to call Flush
        var written = encoder.Flush(buffer);
        await stream.WriteAsync(buffer.AsMemory(0, written), cancellationToken); // use ValueTask overload.
    }

    void WriteCore(ReadOnlySpan<byte> source)
    {
        if (mode != CompressionMode.Compress)
        {
            throw new InvalidOperationException("Write operation must be Compress mode.");
        }

        // Adding headers and footers all the time is redundant, but we prioritize simplicity of implementation.
        var maxDest = encoder.GetMaxCompressedLength(source.Length, includingHeaderAndFooter: true);

        var dest = buffer;
        if (dest == null)
        {
            dest = buffer = ArrayPool<byte>.Shared.Rent(maxDest);
        }
        else if (dest.Length < maxDest)
        {
            ArrayPool<byte>.Shared.Return(dest, clearArray: false);
            dest = buffer = ArrayPool<byte>.Shared.Rent(maxDest);
        }

        var written = encoder.Compress(source, dest, isFinalBlock: false);

        if (written > 0)
        {
            stream.Write(dest, 0, written);
        }
    }

    async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        if (mode != CompressionMode.Compress)
        {
            throw new InvalidOperationException("Write operation must be Compress mode.");
        }

        // Adding headers and footers all the time is redundant, but we prioritize simplicity of implementation.
        var maxDest = encoder.GetMaxCompressedLength(source.Length, includingHeaderAndFooter: true);

        var dest = buffer;
        if (dest == null)
        {
            dest = buffer = ArrayPool<byte>.Shared.Rent(maxDest);
        }
        else if (dest.Length < maxDest)
        {
            ArrayPool<byte>.Shared.Return(dest, clearArray: false);
            dest = buffer = ArrayPool<byte>.Shared.Rent(maxDest);
        }

        var written = encoder.Compress(source.Span, dest, isFinalBlock: false);

        if (written > 0)
        {
            await stream.WriteAsync(dest.AsMemory(0, written), cancellationToken);
        }
    }

    #endregion

    #region Decode

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadCore(new Span<byte>(buffer, offset, count));
    }

    public override int ReadByte()
    {
        byte b = default;
        var read = Read(MemoryMarshal.CreateSpan(ref b, 1));
        return read != 0 ? b : -1;
    }

    public override int Read(Span<byte> buffer)
    {
        return ReadCore(buffer);
    }

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        return TaskToAsyncResultShim.Begin(ReadAsync(buffer, offset, count, CancellationToken.None), callback, state);
    }

    public override int EndRead(IAsyncResult asyncResult)
    {
        return TaskToAsyncResultShim.End<int>(asyncResult);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadCoreAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return ReadCoreAsync(buffer, cancellationToken);
    }

    // TODO:WIP read logic is failed...
    int ReadCore(Span<byte> destination)
    {
        var totalRead = 0;

        // First, return any buffered decoded data
        if (readBufferCount > 0)
        {
            var toCopy = Math.Min(readBufferCount, destination.Length);
            buffer.AsSpan(readBufferOffset, toCopy).CopyTo(destination);

            readBufferOffset += toCopy;
            readBufferCount -= toCopy;
            totalRead += toCopy;

            // all buffered data is read
            if (readBufferCount == 0)
            {
                readBufferOffset = 0;
            }

            destination = destination.Slice(toCopy);
            if (destination.IsEmpty) return totalRead;
        }

        var readBuffer = buffer;
        if (readBuffer == null)
        {
            readBuffer = buffer = ArrayPool<byte>.Shared.Rent(DecoderBufferSize);
        }

#pragma warning disable CA2022 // Avoid inexact read with 'Stream.Read'
        while (destination.Length > 0)
        {
            // TODO: not work correctly
            if (readBufferCount == 0)
            {
            }

            // read compressed data from source stream.
            var bytesRead = stream.Read(readBuffer);
            if (bytesRead == 0)
            {
                break; // End of stream
            }

            var compressedSpan = readBuffer.AsSpan(0, readBufferOffset + bytesRead);

            var status = decoder.Decompress(compressedSpan, destination, out int consumed, out int written);

            readBufferOffset += consumed;
            readBufferCount -= consumed;

            totalRead += written;
            destination = destination.Slice(written);

            if (status == OperationStatus.Done || status == OperationStatus.NeedMoreData)
            {
                // Need more input data
                if (bytesRead == 0) break;
            }
            else if (status == OperationStatus.DestinationTooSmall)
            {
                // Destination is full, we'll continue next time
                break;
            }
        }
#pragma warning restore CA2022

        return totalRead;
    }

    async ValueTask<int> ReadCoreAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        var totalRead = 0;

        // First, return any buffered decoded data
        if (readBufferCount > 0)
        {
            var toCopy = Math.Min(readBufferCount, destination.Length);
            buffer.AsSpan(readBufferOffset, toCopy).CopyTo(destination.Span);

            readBufferOffset += toCopy;
            readBufferCount -= toCopy;
            totalRead += toCopy;

            // all buffered data is read
            if (readBufferCount == 0)
            {
                readBufferOffset = 0;
            }

            destination = destination.Slice(toCopy);
            if (destination.IsEmpty) return totalRead;
        }

        var readBuffer = buffer;
        if (readBuffer == null)
        {
            readBuffer = buffer = ArrayPool<byte>.Shared.Rent(DecoderBufferSize);
        }

        while (destination.Length > 0)
        {
            // read compressed data from source stream.
            var bytesRead = await stream.ReadAsync(readBuffer, cancellationToken);
            if (bytesRead == 0)
            {
                break; // End of stream
            }

            var compressedSpan = readBuffer.AsSpan(0, readBufferOffset + bytesRead);

            var status = decoder.Decompress(compressedSpan, destination.Span, out int consumed, out int written);

            readBufferOffset += consumed;
            readBufferCount -= consumed;

            totalRead += written;
            destination = destination.Slice(written);

            if (status == OperationStatus.Done || status == OperationStatus.NeedMoreData)
            {
                // Need more input data
                if (bytesRead == 0) break;
            }
            else if (status == OperationStatus.DestinationTooSmall)
            {
                // Destination is full, we'll continue next time
                break;
            }
        }

        return totalRead;
    }

    #endregion

    protected override void Dispose(bool disposing)
    {
        if (isDisposed) return;

        try
        {
            if (buffer != null && mode == CompressionMode.Compress)
            {
                // Dispose is called from Close so share implementation.
                var written = encoder.Close(buffer);
                stream.Write(buffer, 0, written);
            }

            if (!leaveOpen)
            {
                stream.Dispose();
            }
        }
        finally
        {
            if (buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            encoder.Dispose();
            decoder.Dispose();

            isDisposed = true;
            base.Dispose(disposing);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (isDisposed) return;

        try
        {
            if (buffer != null && mode == CompressionMode.Compress)
            {
                // Dispose is called from Close so share implementation.
                var written = encoder.Close(buffer);
                await stream.WriteAsync(buffer.AsMemory(0, written));
            }

            if (!leaveOpen)
            {
                stream.Dispose();
            }
        }
        finally
        {
            if (buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            encoder.Dispose();
            decoder.Dispose();

            isDisposed = true;
            base.Dispose();
        }
    }
}
