#pragma warning disable CA2022 // Avoid inexact read with 'Stream.Read'

using NativeCompressions.Internal;
using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace NativeCompressions;

public sealed class ZstandardStream : Stream
#if NETSTANDARD2_0
    , IAsyncDisposable // netstandard2.0 Stream does not implement it
#endif
{
    const int BufferSize = 65536;

    ZstandardEncoder? encoder;
    ZstandardDecoder? decoder;
    CompressionMode mode;
    bool needDisposeNativeCompressor;

    Stream stream;
    bool leaveOpen;
    bool isDisposed;

    byte[]? buffer; // both compress and decompress
    int readBufferOffset; // for decompress
    int readBufferCount; // for decompress
    bool decoderMayHoldOutput; // for decompress, the last call filled the destination so the decoder may still hold output

    public ZstandardStream(Stream stream, CompressionMode mode, bool leaveOpen = false)
    {
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.mode = mode;
        this.needDisposeNativeCompressor = true;

        if (mode == CompressionMode.Decompress)
        {
            this.decoder = new ZstandardDecoder(ZstandardDecompressionOptions.Default);
        }
        else
        {
            this.encoder = new ZstandardEncoder(ZstandardCompressionOptions.Default);
        }
    }

    public ZstandardStream(Stream stream, in ZstandardCompressionOptions options, bool leaveOpen = false)
    {
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.needDisposeNativeCompressor = true;
        this.encoder = new ZstandardEncoder(options);
        this.mode = CompressionMode.Compress;
    }

    public ZstandardStream(Stream stream, in ZstandardDecompressionOptions options, bool leaveOpen = false)
    {
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.needDisposeNativeCompressor = true;
        this.decoder = new ZstandardDecoder(options);
        this.mode = CompressionMode.Decompress;
    }

    public ZstandardStream(Stream stream, CompressionLevel compressionLevel, bool leaveOpen = false)
        : this(stream, ZstandardCompressionOptions.Default with { CompressionLevel = ToCompressionLevel(compressionLevel) }, leaveOpen)
    {
    }

    public ZstandardStream(Stream stream, CompressionMode mode, ZstandardDictionary dictionary, bool leaveOpen = false)
    {
        if (dictionary == null) throw new ArgumentNullException(nameof(dictionary));

        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.mode = mode;
        this.needDisposeNativeCompressor = true;

        if (mode == CompressionMode.Decompress)
        {
            this.decoder = new ZstandardDecoder(ZstandardDecompressionOptions.Default with { Dictionary = dictionary });
        }
        else
        {
            this.encoder = new ZstandardEncoder(ZstandardCompressionOptions.Default with { CompressionLevel = dictionary.CompressionLevel, Dictionary = dictionary });
        }
    }

    // Same mapping as BrotliStream. Zstandard level 0 means default, not "no compression", so NoCompression is rejected.
    // SmallestSize does not exist in netstandard2.1 so match on the numeric values.
    static int ToCompressionLevel(CompressionLevel compressionLevel) => (int)compressionLevel switch
    {
        0 => Zstandard.DefaultCompressionLevel, // Optimal
        1 => 1, // Fastest
        2 => throw new ArgumentException("NoCompression is not supported by Zstandard.", nameof(compressionLevel)),
        3 => Zstandard.MaxCompressionLevel, // SmallestSize
        _ => throw new ArgumentOutOfRangeException(nameof(compressionLevel))
    };

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

    /// <summary>
    /// Declares the total size that will be written so it is recorded in the frame header. Call before the first Write.
    /// </summary>
    public void SetSourceLength(long length)
    {
        ValidateDisposed();
        if (mode != CompressionMode.Compress)
        {
            throw new InvalidOperationException("SetSourceLength requires Compress mode.");
        }
        encoder!.SetSourceLength(length);
    }

    /// <summary>
    /// Initializes a stream that compresses with an encoder owned by the caller. The stream does not dispose the encoder.
    /// </summary>
    /// <remarks>When a write fails, reset the encoder with <see cref="ZstandardEncoder.Reset()"/> before reusing it, or dispose it.</remarks>
    public ZstandardStream(Stream stream, ZstandardEncoder encoder, bool leaveOpen = false)
    {
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.needDisposeNativeCompressor = false;
        this.encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        this.mode = CompressionMode.Compress;
    }

    /// <summary>
    /// Initializes a stream that decompresses with a decoder owned by the caller. The stream does not dispose the decoder.
    /// </summary>
    /// <remarks>When a read fails, reset the decoder with <see cref="ZstandardDecoder.Reset()"/> before reusing it, or dispose it.</remarks>
    public ZstandardStream(Stream stream, ZstandardDecoder decoder, bool leaveOpen = false)
    {
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.needDisposeNativeCompressor = false;
        this.decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        this.mode = CompressionMode.Decompress;
        this.decoderMayHoldOutput = decoder.IsFrameInProgress; // a decoder handed over inside a frame is asked for its output first
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
        buffer ??= ArrayPool<byte>.Shared.Rent(BufferSize);

        var status = OperationStatus.DestinationTooSmall;
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = encoder!.Flush(buffer, out var written);
            stream.Write(buffer, 0, written);
        }
        if (status != OperationStatus.Done)
        {
            throw new InvalidOperationException($"Flush failed: {status}");
        }

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
        buffer ??= ArrayPool<byte>.Shared.Rent(BufferSize);

        var status = OperationStatus.DestinationTooSmall;
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = encoder!.Flush(buffer, out var written);
            await stream.WriteAsync(buffer.AsMemory(0, written), cancellationToken); // use ValueTask overload.
        }
        if (status != OperationStatus.Done)
        {
            throw new InvalidOperationException($"Flush failed: {status}");
        }
        await stream.FlushAsync(cancellationToken);
    }

    void WriteCore(ReadOnlySpan<byte> source)
    {
        ValidateDisposed();
        if (mode != CompressionMode.Compress)
        {
            throw new InvalidOperationException("Write operation must be Compress mode.");
        }

        var dest = buffer;
        if (dest == null)
        {
            dest = buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        }

        var status = OperationStatus.DestinationTooSmall;
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = encoder!.Compress(source, dest, out var consumed, out var written, isFinalBlock: false);
            if (status == OperationStatus.InvalidData)
            {
                throw new InvalidOperationException("Compression failed.");
            }

            source = source.Slice(consumed);
            if (written > 0) // nothing is written while the encoder buffers a block
            {
                stream.Write(dest, 0, written);
            }
        }
    }

    async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        ValidateDisposed();
        if (mode != CompressionMode.Compress)
        {
            throw new InvalidOperationException("Write operation must be Compress mode.");
        }

        var dest = buffer;
        if (dest == null)
        {
            dest = buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        }

        var status = OperationStatus.DestinationTooSmall;
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = encoder!.Compress(source.Span, dest, out var consumed, out var written, isFinalBlock: false);
            if (status == OperationStatus.InvalidData)
            {
                throw new InvalidOperationException("Compression failed.");
            }

            source = source.Slice(consumed);
            if (written > 0) // nothing is written while the encoder buffers a block
            {
                await stream.WriteAsync(dest.AsMemory(0, written), cancellationToken);
            }
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

        buffer ??= ArrayPool<byte>.Shared.Rent(BufferSize);
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
                        throw new ZstandardException("Invalid Zstandard frame: input ends inside a frame.");
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
                    throw new ZstandardException("Invalid Zstandard frame.");

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
                            throw new ZstandardException("Invalid Zstandard frame: input ends inside a frame.");
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

        buffer ??= ArrayPool<byte>.Shared.Rent(BufferSize);
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
                        throw new ZstandardException("Invalid Zstandard frame: input ends inside a frame.");
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
                    throw new ZstandardException("Invalid Zstandard frame.");

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
                            throw new ZstandardException("Invalid Zstandard frame: input ends inside a frame.");
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
            // also without any Write, so an empty source still produces a valid frame
            if (mode == CompressionMode.Compress)
            {
                try
                {
                    buffer ??= ArrayPool<byte>.Shared.Rent(BufferSize);
                    var status = OperationStatus.DestinationTooSmall;
                    while (status == OperationStatus.DestinationTooSmall)
                    {
                        status = encoder!.Close(buffer, out var written);
                        stream.Write(buffer, 0, written);
                    }
                    if (status != OperationStatus.Done)
                    {
                        // for example a SetSourceLength that the written data did not match; silently dropping the frame would lose data
                        closeFailure = new InvalidOperationException("Compression failed while closing the frame.");
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
                    buffer ??= ArrayPool<byte>.Shared.Rent(BufferSize);
                    var status = OperationStatus.DestinationTooSmall;
                    while (status == OperationStatus.DestinationTooSmall)
                    {
                        status = encoder!.Close(buffer, out var written);
                        await stream.WriteAsync(buffer.AsMemory(0, written));
                    }
                    if (status != OperationStatus.Done)
                    {
                        closeFailure = new InvalidOperationException("Compression failed while closing the frame.");
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

