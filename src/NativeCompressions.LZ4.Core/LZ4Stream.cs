#pragma warning disable CA2022 // Avoid inexact read with 'Stream.Read'

using NativeCompressions.Internal;
using System.Buffers;
using System.Data;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace NativeCompressions;

public sealed class LZ4Stream : Stream
#if NETSTANDARD2_0
    , IAsyncDisposable // netstandard2.0 Stream does not implement it
#endif
{
    const int EncoderBufferSize = 8192;
    const int DecoderInputBufferSize = 65536;
    const int DecoderOutputBufferSize = 8192;

    LZ4Encoder? encoder;
    LZ4Decoder? decoder;
    CompressionMode mode;
    bool needDisposeNativeCompressor;

    Stream stream;
    bool leaveOpen;
    bool isDisposed;

    byte[]? buffer; // both compress and decompress
    byte[]? writeBuffer; // coalesces small writes before crossing the native boundary
    int writeBufferCount;
    int readBufferOffset; // for decompress
    int readBufferCount; // for decompress
    byte[]? decompressedBuffer; // serves small reads without repeatedly entering native code
    int decompressedBufferOffset;
    int decompressedBufferCount;

    public LZ4Stream(Stream stream, CompressionMode mode, bool leaveOpen = false)
    {
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.mode = mode;
        this.needDisposeNativeCompressor = true;

        if (mode == CompressionMode.Decompress)
        {
            this.decoder = new LZ4Decoder(LZ4DecompressionOptions.Default);
        }
        else
        {
            this.encoder = new LZ4Encoder(LZ4CompressionOptions.Default);
        }
    }

    public LZ4Stream(Stream stream, in LZ4CompressionOptions options, bool leaveOpen = false)
    {
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.needDisposeNativeCompressor = true;
        this.encoder = new LZ4Encoder(options);
        this.mode = CompressionMode.Compress;
    }

    public LZ4Stream(Stream stream, in LZ4DecompressionOptions options, bool leaveOpen = false)
    {
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.needDisposeNativeCompressor = true;
        this.decoder = new LZ4Decoder(options);
        this.mode = CompressionMode.Decompress;
    }

    public LZ4Stream(Stream stream, LZ4Encoder encoder, bool leaveOpen = false)
    {
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.needDisposeNativeCompressor = false;
        this.encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        this.mode = CompressionMode.Compress;
    }

    public LZ4Stream(Stream stream, LZ4Decoder decoder, bool leaveOpen = false)
    {
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.needDisposeNativeCompressor = false;
        this.decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        this.mode = CompressionMode.Decompress;
    }

    public override bool CanRead => mode == CompressionMode.Decompress && stream.CanRead;
    public override bool CanWrite => mode == CompressionMode.Compress && stream.CanWrite;
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

#if NETSTANDARD2_0
    public void Write(ReadOnlySpan<byte> buffer) // not virtual on netstandard2.0 Stream
#else
    public override void Write(ReadOnlySpan<byte> buffer)
#endif
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

#if NETSTANDARD2_0
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) // not virtual on netstandard2.0 Stream
#else
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
#endif
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

        FlushWriteBuffer();
        if (buffer == null) return;

        // Write acquire max GetMaxCompressedLength per source so buffer size is safe to call Flush
        var written = encoder!.Flush(buffer);
        stream.Write(buffer, 0, written);

        stream.Flush();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ValidateDisposed();
        if (mode != CompressionMode.Compress)
        {
            throw new InvalidOperationException("Write operation must be Compress mode.");
        }
        await FlushWriteBufferAsync(cancellationToken);
        if (buffer == null) return;

        // Write acquire max GetMaxCompressedLength per source so buffer size is safe to call Flush
        var written = encoder!.Flush(buffer);
        await stream.WriteAsync(buffer.AsMemory(0, written), cancellationToken); // use ValueTask overload.

        await stream.FlushAsync(cancellationToken);
    }

    void WriteCore(ReadOnlySpan<byte> source)
    {
        ValidateDisposed();
        if (mode != CompressionMode.Compress)
        {
            throw new InvalidOperationException("Write operation must be Compress mode.");
        }

        if (source.IsEmpty || buffer == null)
        {
            CompressCore(source);
            return;
        }

        if (writeBufferCount > 0)
        {
            var copied = Math.Min(source.Length, EncoderBufferSize - writeBufferCount);
            source[..copied].CopyTo(writeBuffer.AsSpan(writeBufferCount));
            writeBufferCount += copied;
            source = source[copied..];

            if (writeBufferCount == EncoderBufferSize)
            {
                FlushWriteBuffer();
            }
        }

        if (source.Length >= EncoderBufferSize)
        {
            CompressCore(source);
            return;
        }

        if (!source.IsEmpty)
        {
            writeBuffer ??= ArrayPool<byte>.Shared.Rent(EncoderBufferSize);
            source.CopyTo(writeBuffer);
            writeBufferCount = source.Length;
        }
    }

    void CompressCore(ReadOnlySpan<byte> source)
    {
        var maxDest = encoder!.GetMaxCompressedLength(source.Length);
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
        if (written != 0)
        {
            stream.Write(dest, 0, written);
        }
    }

    void FlushWriteBuffer()
    {
        if (writeBufferCount == 0) return;

        CompressCore(writeBuffer.AsSpan(0, writeBufferCount));
        writeBufferCount = 0;
    }

    async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        ValidateDisposed();
        if (mode != CompressionMode.Compress)
        {
            throw new InvalidOperationException("Write operation must be Compress mode.");
        }

        if (source.IsEmpty || buffer == null)
        {
            await CompressCoreAsync(source, cancellationToken);
            return;
        }

        if (writeBufferCount > 0)
        {
            var copied = Math.Min(source.Length, EncoderBufferSize - writeBufferCount);
            source.Span[..copied].CopyTo(writeBuffer.AsSpan(writeBufferCount));
            writeBufferCount += copied;
            source = source[copied..];

            if (writeBufferCount == EncoderBufferSize)
            {
                await FlushWriteBufferAsync(cancellationToken);
            }
        }

        if (source.Length >= EncoderBufferSize)
        {
            await CompressCoreAsync(source, cancellationToken);
            return;
        }

        if (!source.IsEmpty)
        {
            writeBuffer ??= ArrayPool<byte>.Shared.Rent(EncoderBufferSize);
            source.Span.CopyTo(writeBuffer);
            writeBufferCount = source.Length;
        }
    }

    async ValueTask CompressCoreAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        var maxDest = encoder!.GetMaxCompressedLength(source.Length);
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
        if (written != 0)
        {
            await stream.WriteAsync(dest.AsMemory(0, written), cancellationToken);
        }
    }

    async ValueTask FlushWriteBufferAsync(CancellationToken cancellationToken)
    {
        if (writeBufferCount == 0) return;

        await CompressCoreAsync(writeBuffer.AsMemory(0, writeBufferCount), cancellationToken);
        writeBufferCount = 0;
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
        Span<byte> span = stackalloc byte[1];
        var read = Read(span);
        return read != 0 ? span[0] : -1;
    }

#if NETSTANDARD2_0
    public int Read(Span<byte> buffer) // not virtual on netstandard2.0 Stream
#else
    public override int Read(Span<byte> buffer)
#endif
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

#if NETSTANDARD2_0
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) // not virtual on netstandard2.0 Stream
#else
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
#endif
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

        var totalRead = CopyDecompressedBuffer(destination);
        if (totalRead != 0) return totalRead;

        if (destination.Length >= DecoderOutputBufferSize)
        {
            return ReadDecompressedCore(destination, returnAfterOutput: false);
        }

        decompressedBuffer ??= ArrayPool<byte>.Shared.Rent(DecoderOutputBufferSize);
        decompressedBufferCount = ReadDecompressedCore(decompressedBuffer, returnAfterOutput: true);
        decompressedBufferOffset = 0;

        var copied = CopyDecompressedBuffer(destination);
        return copied;
    }

    int CopyDecompressedBuffer(Span<byte> destination)
    {
        var copied = Math.Min(destination.Length, decompressedBufferCount);
        if (copied == 0) return 0;

        decompressedBuffer.AsSpan(decompressedBufferOffset, copied).CopyTo(destination);
        decompressedBufferOffset += copied;
        decompressedBufferCount -= copied;
        return copied;
    }

    int ReadDecompressedCore(Span<byte> destination, bool returnAfterOutput)
    {
        buffer ??= ArrayPool<byte>.Shared.Rent(DecoderInputBufferSize);
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

            var status = decoder!.Decompress(source, destination, out var consumed, out var written);

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
                case OperationStatus.InvalidData:
                    throw new InvalidOperationException("Decompression failed: the input is not valid LZ4 frame data.");

                case OperationStatus.Done:
                    // Frame completed, there might be another frame so continue
                    decoder!.Reset();
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

                    if (returnAfterOutput && totalRead > 0)
                    {
                        return totalRead;
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

        var totalRead = CopyDecompressedBuffer(destination.Span);
        if (totalRead != 0) return totalRead;

        if (destination.Length >= DecoderOutputBufferSize)
        {
            return await ReadDecompressedCoreAsync(destination, cancellationToken, returnAfterOutput: false);
        }

        decompressedBuffer ??= ArrayPool<byte>.Shared.Rent(DecoderOutputBufferSize);
        decompressedBufferCount = await ReadDecompressedCoreAsync(decompressedBuffer, cancellationToken, returnAfterOutput: true);
        decompressedBufferOffset = 0;

        var copied = CopyDecompressedBuffer(destination.Span);
        return copied;
    }

    async ValueTask<int> ReadDecompressedCoreAsync(Memory<byte> destination, CancellationToken cancellationToken, bool returnAfterOutput)
    {
        buffer ??= ArrayPool<byte>.Shared.Rent(DecoderInputBufferSize);
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

            var status = decoder!.Decompress(source, destination.Span, out var consumed, out var written);

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
                case OperationStatus.InvalidData:
                    throw new InvalidOperationException("Decompression failed: the input is not valid LZ4 frame data.");

                case OperationStatus.Done:
                    // Frame completed, there might be another frame so continue
                    decoder!.Reset();
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

                    if (returnAfterOutput && totalRead > 0)
                    {
                        return totalRead;
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

        // A failed Close is reported after everything owned by this stream is released, so a bad frame never leaks the inner stream.
        Exception? closeFailure = null;
        try
        {
            if (mode == CompressionMode.Compress)
            {
                try
                {
                    FlushWriteBuffer();
                    if (buffer != null)
                    {
                        var written = encoder!.Close(buffer);
                        stream.Write(buffer, 0, written);
                    }
                }
                catch (Exception ex)
                {
                    closeFailure = ex;
                }
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
            if (writeBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(writeBuffer);
            }
            if (decompressedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(decompressedBuffer);
            }

            if (needDisposeNativeCompressor)
            {
                encoder?.Dispose();
                decoder?.Dispose();
            }

            isDisposed = true;
            base.Dispose(disposing);
        }

        if (closeFailure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeFailure).Throw();
        }
    }

#if NETSTANDARD2_0
    public async ValueTask DisposeAsync() // not virtual on netstandard2.0 Stream
#else
    public override async ValueTask DisposeAsync()
#endif
    {
        if (isDisposed) return;

        Exception? closeFailure = null;
        try
        {
            if (mode == CompressionMode.Compress)
            {
                try
                {
                    await FlushWriteBufferAsync(CancellationToken.None);
                    if (buffer != null)
                    {
                        var written = encoder!.Close(buffer);
                        await stream.WriteAsync(buffer.AsMemory(0, written));
                    }
                }
                catch (Exception ex)
                {
                    closeFailure = ex;
                }
            }

            if (!leaveOpen)
            {
                await stream.DisposeAsync();
            }
        }
        finally
        {
            if (buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            if (writeBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(writeBuffer);
            }
            if (decompressedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(decompressedBuffer);
            }

            if (needDisposeNativeCompressor)
            {
                encoder?.Dispose();
                decoder?.Dispose();
            }

            isDisposed = true;
            base.Dispose();
        }

        if (closeFailure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeFailure).Throw();
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
