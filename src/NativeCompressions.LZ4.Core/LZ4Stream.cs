#pragma warning disable CA2022 // Avoid inexact read with 'Stream.Read'

using NativeCompressions.Internal;
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

    public LZ4Stream(Stream stream, CompressionMode mode, LZ4CompressionDictionary? compressionDictionary = null, bool leaveOpen = false)
    {
        this.mode = mode;
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.readBufferCount = 0;

        if (mode == CompressionMode.Decompress)
        {
            this.decoder = new LZ4Decoder(compressionDictionary);
        }
        else
        {
            this.encoder = new LZ4Encoder(LZ4FrameOptions.Default, compressionDictionary);
        }
    }

    public LZ4Stream(Stream stream, in LZ4FrameOptions frameOptions, LZ4CompressionDictionary? compressionDictionary = null, bool leaveOpen = false)
    {
        this.mode = CompressionMode.Compress;
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.readBufferCount = 0;
        this.encoder = new LZ4Encoder(frameOptions, compressionDictionary);
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
        ValidateDisposed();
        WriteCore(new ReadOnlySpan<byte>(buffer, offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ValidateDisposed();
        WriteCore(buffer);
    }

    public override void WriteByte(byte value)
    {
        ValidateDisposed();
        Span<byte> span = stackalloc byte[1];
        span[0] = value;
        WriteCore(span);
    }

    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        ValidateDisposed();
        return TaskToAsyncResult.Begin(WriteAsync(buffer, offset, count, CancellationToken.None), callback, state);
    }

    public override void EndWrite(IAsyncResult asyncResult)
    {
        ValidateDisposed();
        TaskToAsyncResult.End(asyncResult);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateDisposed();
        return WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ValidateDisposed();
        return cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled(cancellationToken)
            : WriteCoreAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
        ValidateDisposed();
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
        ValidateDisposed();
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
        ValidateDisposed();
        if (mode != CompressionMode.Compress)
        {
            throw new InvalidOperationException("Write operation must be Compress mode.");
        }

        // Adding headers and footers all the time is redundant, but we prioritize simplicity of implementation.
        var maxDest = encoder.GetMaxCompressedLength(source.Length);

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

        var written = encoder.Compress(source, dest);

        if (written > 0)
        {
            stream.Write(dest, 0, written);
        }
    }

    async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        ValidateDisposed();
        if (mode != CompressionMode.Compress)
        {
            throw new InvalidOperationException("Write operation must be Compress mode.");
        }

        // Adding headers and footers all the time is redundant, but we prioritize simplicity of implementation.
        var maxDest = encoder.GetMaxCompressedLength(source.Length);

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

        var written = encoder.Compress(source.Span, dest);

        if (written > 0)
        {
            await stream.WriteAsync(dest.AsMemory(0, written), cancellationToken);
        }
    }

    #endregion

    #region Decode

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateDisposed();
        return ReadCore(new Span<byte>(buffer, offset, count));
    }

    public override int ReadByte()
    {
        ValidateDisposed();
        byte b = default;
        var read = Read(MemoryMarshal.CreateSpan(ref b, 1));
        return read != 0 ? b : -1;
    }

    public override int Read(Span<byte> buffer)
    {
        ValidateDisposed();
        return ReadCore(buffer);
    }

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        ValidateDisposed();
        return TaskToAsyncResult.Begin(ReadAsync(buffer, offset, count, CancellationToken.None), callback, state);
    }

    public override int EndRead(IAsyncResult asyncResult)
    {
        ValidateDisposed();
        return TaskToAsyncResult.End<int>(asyncResult);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateDisposed();
        return ReadCoreAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ValidateDisposed();
        return ReadCoreAsync(buffer, cancellationToken);
    }

    int ReadCore(Span<byte> destination)
    {
        if (destination.IsEmpty) return 0;
        if (mode != CompressionMode.Decompress)
        {
            throw new InvalidOperationException("Read operation must be Decompress mode.");
        }

        buffer ??= ArrayPool<byte>.Shared.Rent(DecoderBufferSize);
        var totalRead = 0;

        while (destination.Length > 0)
        {
            ReadOnlySpan<byte> source;

            if (readBufferCount > 0)
            {
                // Use existing buffered data
                source = buffer.AsSpan(readBufferOffset, readBufferCount);
            }
            else
            {
                // Buffer is empty, first flush decoder's internal buffer
                source = ReadOnlySpan<byte>.Empty;
            }

            var status = decoder.Decompress(source, destination, out var consumed, out var written);

            // Update buffer state
            if (consumed > 0)
            {
                readBufferOffset += consumed;
                readBufferCount -= consumed;
            }

            if (written > 0)
            {
                totalRead += written;
                destination = destination.Slice(written);
            }

            switch (status)
            {
                case OperationStatus.Done:
                    // Frame completed, there might be another frame so continue
                    decoder.Reset();
                    break;

                case OperationStatus.DestinationTooSmall:
                    // Output buffer is full
                    return totalRead;

                case OperationStatus.NeedMoreData:
                    // Need more data

                    // If written > 0, decoder likely has more data in its internal buffer
                    // Exhaust the internal buffer before reading new data
                    if (written > 0)
                    {
                        // Decoder produced output, retry in next loop
                        // Don't read additional data
                        break;
                    }

                    // Only consider reading new data when written == 0
                    if (readBufferCount == 0)
                    {
                        // Buffer was completely consumed or was originally empty
                        // Decoder's internal buffer is also empty, so read new data
                        readBufferOffset = 0;
                        readBufferCount = stream.Read(buffer, 0, buffer.Length);

                        if (readBufferCount == 0)
                        {
                            // Truly reached EOF
                            return totalRead;
                        }
                    }
                    else
                    {
                        // readBufferCount > 0: still have unconsumed data
                        // This happens when data was partially consumed (incomplete block header, etc.)

                        // Move unconsumed data to the beginning of buffer
                        if (readBufferOffset > 0)
                        {
                            Buffer.BlockCopy(buffer, readBufferOffset, buffer, 0, readBufferCount);
                        }

                        // Read additional data
                        var bytesRead = stream.Read(buffer, readBufferCount, buffer.Length - readBufferCount);

                        readBufferOffset = 0;
                        readBufferCount += bytesRead;

                        if (bytesRead == 0)
                        {
                            // No more data available
                            // Possibly incomplete frame at end
                            return totalRead;
                        }
                    }
                    break;
            }
        }

        return totalRead;
    }

    async ValueTask<int> ReadCoreAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.IsEmpty) return 0;
        if (mode != CompressionMode.Decompress)
        {
            throw new InvalidOperationException("Read operation must be Decompress mode.");
        }

        buffer ??= ArrayPool<byte>.Shared.Rent(DecoderBufferSize);
        var totalRead = 0;

        while (destination.Length > 0)
        {
            ReadOnlySpan<byte> source;

            if (readBufferCount > 0)
            {
                // Use existing buffered data
                source = buffer.AsSpan(readBufferOffset, readBufferCount);
            }
            else
            {
                // Buffer is empty, first flush decoder's internal buffer
                source = ReadOnlySpan<byte>.Empty;
            }

            var status = decoder.Decompress(source, destination.Span, out var consumed, out var written);

            // Update buffer state
            if (consumed > 0)
            {
                readBufferOffset += consumed;
                readBufferCount -= consumed;
            }

            if (written > 0)
            {
                totalRead += written;
                destination = destination.Slice(written);
            }

            switch (status)
            {
                case OperationStatus.Done:
                    // Frame completed, there might be another frame so continue
                    decoder.Reset();
                    break;

                case OperationStatus.DestinationTooSmall:
                    // Output buffer is full
                    return totalRead;

                case OperationStatus.NeedMoreData:
                    // Need more data

                    // If written > 0, decoder likely has more data in its internal buffer
                    // Exhaust the internal buffer before reading new data
                    if (written > 0)
                    {
                        // Decoder produced output, retry in next loop
                        // Don't read additional data
                        break;
                    }

                    // Only consider reading new data when written == 0
                    if (readBufferCount == 0)
                    {
                        // Buffer was completely consumed or was originally empty
                        // Decoder's internal buffer is also empty, so read new data
                        readBufferOffset = 0;
                        readBufferCount = await stream.ReadAsync(
                            buffer.AsMemory(0, buffer.Length), cancellationToken);

                        if (readBufferCount == 0)
                        {
                            // Truly reached EOF
                            return totalRead;
                        }
                    }
                    else
                    {
                        // readBufferCount > 0: still have unconsumed data
                        // This happens when data was partially consumed (incomplete block header, etc.)

                        // Move unconsumed data to the beginning of buffer
                        if (readBufferOffset > 0)
                        {
                            Buffer.BlockCopy(buffer, readBufferOffset, buffer, 0, readBufferCount);
                        }

                        // Read additional data
                        var bytesRead = await stream.ReadAsync(
                            buffer.AsMemory(readBufferCount, buffer.Length - readBufferCount),
                            cancellationToken);

                        readBufferOffset = 0;
                        readBufferCount += bytesRead;

                        if (bytesRead == 0)
                        {
                            // No more data available
                            // Possibly incomplete frame at end
                            return totalRead;
                        }
                    }
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

    void ValidateDisposed()
    {
        if (isDisposed)
        {
            Throws.ObjectDisposedException();
        }
    }
}

