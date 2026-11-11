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
    const int DecoderBufferSize = 65536;

    // A write up to this size reaches the encoder in one call, a larger one in pieces of about this size.
    // lz4 copies the last 64KB aside after every call with linked blocks, so pieces of a block or two cost speed and some ratio.
    const int MaxEncoderInputLength = 1024 * 1024;

    LZ4Encoder? encoder;
    LZ4Decoder? decoder;
    CompressionMode mode;
    bool needDisposeNativeCompressor;

    Stream stream;
    bool leaveOpen;
    bool isDisposed;

    byte[]? buffer; // both compress and decompress
    int readBufferOffset; // for decompress
    int readBufferCount; // for decompress
    bool decoderMayHoldOutput; // for decompress, the last call filled the destination so the decoder may still hold output

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
        this.decoder = new LZ4Decoder(options.WithoutStableDst());
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

    /// <exception cref="ArgumentException">The decoder was created with <see cref="LZ4DecompressionOptions.StableDst"/>. Buffers handed to Read do not stay at one address, so such a decoder cannot be used by a stream.</exception>
    public LZ4Stream(Stream stream, LZ4Decoder decoder, bool leaveOpen = false)
    {
        if (decoder == null) throw new ArgumentNullException(nameof(decoder));
        if (decoder.StableDst) throw new ArgumentException("A decoder created with StableDst cannot be used by a stream. Buffers handed to Read do not stay at one address.", nameof(decoder));

        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.needDisposeNativeCompressor = false;
        this.decoder = decoder;
        this.mode = CompressionMode.Decompress;
        this.decoderMayHoldOutput = decoder.IsFrameInProgress; // a decoder handed over inside a frame is asked for its output first
    }

    /// <summary>
    /// Gets the underlying stream.
    /// </summary>
    public Stream BaseStream
    {
        get
        {
            ValidateDisposed();
            return stream;
        }
    }

    public override bool CanRead => !isDisposed && mode == CompressionMode.Decompress && stream.CanRead;
    public override bool CanWrite => !isDisposed && mode == CompressionMode.Compress && stream.CanWrite;
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
            return; // nothing to flush while decompressing, the same as the BCL streams
        }

        // An encoder handed in by the caller may hold data this stream never saw, so the buffer is rented here too.
        var dest = EnsureEncoderBuffer();

        var status = OperationStatus.DestinationTooSmall;
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = encoder!.Flush(dest, out var written);
            stream.Write(dest, 0, written);
        }
        ThrowIfFailed(status, FlushFailed);

        stream.Flush();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ValidateDisposed();
        if (mode != CompressionMode.Compress)
        {
            return; // nothing to flush while decompressing, the same as the BCL streams
        }

        // Checked before the encoder is touched. Output taken from the encoder cannot be put back,
        // so a cancelled write after that would lose it.
        cancellationToken.ThrowIfCancellationRequested();

        // An encoder handed in by the caller may hold data this stream never saw, so the buffer is rented here too.
        var dest = EnsureEncoderBuffer();

        var status = OperationStatus.DestinationTooSmall;
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = encoder!.Flush(dest, out var written);
            await stream.WriteAsync(dest.AsMemory(0, written), cancellationToken); // use ValueTask overload.
        }
        ThrowIfFailed(status, FlushFailed);

        await stream.FlushAsync(cancellationToken);
    }

    void WriteCore(ReadOnlySpan<byte> source)
    {
        ValidateDisposed();
        if (mode != CompressionMode.Compress)
        {
            throw new InvalidOperationException("Write operation must be Compress mode.");
        }

        // a write larger than the buffer goes out in several calls
        var dest = EnsureEncoderBuffer(source.Length);
        var status = OperationStatus.DestinationTooSmall;
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = encoder!.Compress(source, dest, out var consumed, out var written, isFinalBlock: false);
            source = source.Slice(consumed);
            if (written > 0) stream.Write(dest, 0, written);
        }
        ThrowIfFailed(status, CompressFailed);
    }

    async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        ValidateDisposed();
        if (mode != CompressionMode.Compress)
        {
            throw new InvalidOperationException("Write operation must be Compress mode.");
        }

        // a write larger than the buffer goes out in several calls
        var dest = EnsureEncoderBuffer(source.Length);
        var status = OperationStatus.DestinationTooSmall;
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = encoder!.Compress(source.Span, dest, out var consumed, out var written, isFinalBlock: false);
            source = source.Slice(consumed);
            if (written > 0) await stream.WriteAsync(dest.AsMemory(0, written), cancellationToken);
        }
        ThrowIfFailed(status, CompressFailed);
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
        if (mode != CompressionMode.Decompress)
        {
            throw new InvalidOperationException("Read operation must be Decompress mode.");
        }

        buffer ??= ArrayPool<byte>.Shared.Rent(DecoderBufferSize);
        var totalRead = 0;

        if (destination.IsEmpty)
        {
            // A zero-byte read waits until input is available, the way the System.IO.Compression streams do,
            // so callers can delay renting a buffer. Buffered input or output held by the decoder counts as
            // available, otherwise the inner stream is read once. End of input is reported by the next real read.
            if (readBufferCount == 0 && !decoderMayHoldOutput)
            {
                readBufferOffset = 0;
                readBufferCount = stream.Read(buffer, 0, buffer.Length);
            }
            return 0;
        }

        while (destination.Length > 0)
        {
            if (readBufferCount == 0 && !decoderMayHoldOutput)
            {
                // Data decoded so far is returned before asking for more input.
                // The inner stream may not deliver more until the caller has acted on this data.
                if (totalRead > 0)
                {
                    return totalRead;
                }

                // Input is read before the decoder is called. The decoder is called without input only to take out
                // output it holds, because the native decoder counts calls that can do nothing and gives up after a few.
                // A read that waits for input and is cancelled or fails must not add to that count.
                readBufferOffset = 0;
                readBufferCount = stream.Read(buffer, 0, buffer.Length);

                if (readBufferCount == 0)
                {
                    if (decoder!.IsFrameInProgress)
                    {
                        throw new LZ4Exception("Invalid LZ4 frame: input ends inside a frame.");
                    }

                    return totalRead;
                }
            }

            // empty when only the output held by the decoder is taken out
            var status = decoder!.Decompress(buffer.AsSpan(readBufferOffset, readBufferCount), destination, out var consumed, out var written);
            decoderMayHoldOutput = status == OperationStatus.DestinationTooSmall;

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
                    throw new LZ4Exception("Invalid LZ4 frame.");

                case OperationStatus.Done:
                    // Frame completed, there might be another frame so continue
                    decoder!.Reset();
                    break;

                case OperationStatus.DestinationTooSmall:
                    // Output buffer is full
                    return totalRead;

                case OperationStatus.NeedMoreData:
                    // The input was taken completely, or the decoder produced output and goes on with the rest.
                    // Either way the next turn of the loop continues, and reads more input when the buffer is empty.
                    if (readBufferCount == 0 || written > 0)
                    {
                        break;
                    }

                    // Input is left and nothing was written, the decoder wants it together with what follows.
                    // Data decoded so far is returned first.
                    if (totalRead > 0)
                    {
                        return totalRead;
                    }

                    {
                        // Move unconsumed data to the beginning of buffer. The offset is updated before the read,
                        // so a read that throws leaves the buffer state valid.
                        if (readBufferOffset > 0)
                        {
                            Buffer.BlockCopy(buffer, readBufferOffset, buffer, 0, readBufferCount);
                            readBufferOffset = 0;
                        }

                        // Read additional data
                        var bytesRead = stream.Read(buffer, readBufferCount, buffer.Length - readBufferCount);
                        readBufferCount += bytesRead;

                        if (bytesRead == 0)
                        {
                            throw new LZ4Exception("Invalid LZ4 frame: input ends inside a frame.");
                        }
                    }
                    break;
            }
        }

        return totalRead;
    }

    async ValueTask<int> ReadCoreAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (mode != CompressionMode.Decompress)
        {
            throw new InvalidOperationException("Read operation must be Decompress mode.");
        }

        // checked before the decoder is touched, a cancelled read must not consume input
        cancellationToken.ThrowIfCancellationRequested();

        buffer ??= ArrayPool<byte>.Shared.Rent(DecoderBufferSize);
        var totalRead = 0;

        if (destination.IsEmpty)
        {
            // A zero-byte read waits until input is available, the way the System.IO.Compression streams do,
            // so callers can delay renting a buffer. Buffered input or output held by the decoder counts as
            // available, otherwise the inner stream is read once. End of input is reported by the next real read.
            if (readBufferCount == 0 && !decoderMayHoldOutput)
            {
                readBufferOffset = 0;
                readBufferCount = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            }
            return 0;
        }

        while (destination.Length > 0)
        {
            if (readBufferCount == 0 && !decoderMayHoldOutput)
            {
                // Data decoded so far is returned before asking for more input.
                // The inner stream may not deliver more until the caller has acted on this data.
                if (totalRead > 0)
                {
                    return totalRead;
                }

                // Input is read before the decoder is called. The decoder is called without input only to take out
                // output it holds, because the native decoder counts calls that can do nothing and gives up after a few.
                // A read that waits for input and is cancelled or fails must not add to that count.
                readBufferOffset = 0;
                readBufferCount = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

                if (readBufferCount == 0)
                {
                    if (decoder!.IsFrameInProgress)
                    {
                        throw new LZ4Exception("Invalid LZ4 frame: input ends inside a frame.");
                    }

                    return totalRead;
                }
            }

            // empty when only the output held by the decoder is taken out
            var status = decoder!.Decompress(buffer.AsSpan(readBufferOffset, readBufferCount), destination.Span, out var consumed, out var written);
            decoderMayHoldOutput = status == OperationStatus.DestinationTooSmall;

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
                    throw new LZ4Exception("Invalid LZ4 frame.");

                case OperationStatus.Done:
                    // Frame completed, there might be another frame so continue
                    decoder!.Reset();
                    break;

                case OperationStatus.DestinationTooSmall:
                    // Output buffer is full
                    return totalRead;

                case OperationStatus.NeedMoreData:
                    // The input was taken completely, or the decoder produced output and goes on with the rest.
                    // Either way the next turn of the loop continues, and reads more input when the buffer is empty.
                    if (readBufferCount == 0 || written > 0)
                    {
                        break;
                    }

                    // Input is left and nothing was written, the decoder wants it together with what follows.
                    // Data decoded so far is returned first.
                    if (totalRead > 0)
                    {
                        return totalRead;
                    }

                    {
                        // Move unconsumed data to the beginning of buffer. The offset is updated before the read,
                        // so a read that throws leaves the buffer state valid.
                        if (readBufferOffset > 0)
                        {
                            Buffer.BlockCopy(buffer, readBufferOffset, buffer, 0, readBufferCount);
                            readBufferOffset = 0;
                        }

                        // Read additional data
                        var bytesRead = await stream.ReadAsync(buffer.AsMemory(readBufferCount, buffer.Length - readBufferCount), cancellationToken);
                        readBufferCount += bytesRead;

                        if (bytesRead == 0)
                        {
                            throw new LZ4Exception("Invalid LZ4 frame: input ends inside a frame.");
                        }
                    }
                    break;
            }
        }

        return totalRead;
    }

    #endregion

    const string CompressFailed = "LZ4 compression failed, or the encoder handed to the stream needs Reset.";
    const string FlushFailed = "Flushing the LZ4 frame failed, or the encoder handed to the stream needs Reset.";
    const string CloseFailed = "The LZ4 frame could not be closed, for example the written length differs from ContentSize.";

    // The encoder reports InvalidData when lz4 fails, the stream throws so a broken frame is never taken for a written one.
    // Bad options already throw with the lz4 error when the encoder is created.
    static void ThrowIfFailed(OperationStatus status, string message)
    {
        if (status != OperationStatus.Done)
        {
            throw new LZ4Exception(message);
        }
    }

    // Room for the write up to MaxEncoderInputLength, and at least a block, which keeps the blocks full
    // and lets Flush and Close finish in one call. The buffer grows with the writes and is shared by Write, Flush and Close.
    // An encoder handed in by the caller can be Reset to a larger block size, so the length is checked every time.
    // A write does not end the frame, so the room for ending it is left out, the block floor covers Close.
    byte[] EnsureEncoderBuffer(int writeLength = 0)
    {
        var length = Math.Max(encoder!.GetBlockBufferLength(), encoder.GetMaxCompressedLength(Math.Min(writeLength, MaxEncoderInputLength), includingFooter: false));
        var current = buffer;
        if (current != null && current.Length >= length) return current;

        if (current != null)
        {
            buffer = null;
            ArrayPool<byte>.Shared.Return(current);
        }
        return buffer = ArrayPool<byte>.Shared.Rent(length);
    }

    protected override void Dispose(bool disposing)
    {
        if (isDisposed) return;

        // A failed Close is reported after everything owned by this stream is released, so a bad frame never leaks the inner stream.
        Exception? closeFailure = null;
        try
        {
            // also without any Write, so an empty source still produces a valid frame
            if (mode == CompressionMode.Compress)
            {
                try
                {
                    var dest = EnsureEncoderBuffer();
                    var status = OperationStatus.DestinationTooSmall;
                    while (status == OperationStatus.DestinationTooSmall)
                    {
                        status = encoder!.Close(dest, out var written);
                        stream.Write(dest, 0, written);
                    }
                    ThrowIfFailed(status, CloseFailed); // dropping the frame silently would lose data
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
            // also without any Write, so an empty source still produces a valid frame
            if (mode == CompressionMode.Compress)
            {
                try
                {
                    var dest = EnsureEncoderBuffer();
                    var status = OperationStatus.DestinationTooSmall;
                    while (status == OperationStatus.DestinationTooSmall)
                    {
                        status = encoder!.Close(dest, out var written);
                        await stream.WriteAsync(dest.AsMemory(0, written));
                    }
                    ThrowIfFailed(status, CloseFailed);
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
