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

    public ZstandardStream(Stream stream, in ZstandardCompressionOptions compressionOptions, bool leaveOpen = false)
    {
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.needDisposeNativeCompressor = true;
        this.encoder = new ZstandardEncoder(compressionOptions);
        this.mode = CompressionMode.Compress;
    }

    public ZstandardStream(Stream stream, in ZstandardDecompressionOptions decompressionOptions, bool leaveOpen = false)
    {
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.needDisposeNativeCompressor = true;
        this.decoder = new ZstandardDecoder(decompressionOptions);
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

    public ZstandardStream(Stream stream, ZstandardEncoder encoder, bool leaveOpen = false)
    {
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.needDisposeNativeCompressor = false;
        this.encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        this.mode = CompressionMode.Compress;
    }

    public ZstandardStream(Stream stream, ZstandardDecoder decoder, bool leaveOpen = false)
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

        if (buffer == null) return;

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
            throw new InvalidOperationException("Write operation must be Compress mode.");
        }
        if (buffer == null) return;

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

        buffer ??= ArrayPool<byte>.Shared.Rent(BufferSize);
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
                    throw new InvalidOperationException("Decompression failed: the input is not valid Zstandard data.");

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

        buffer ??= ArrayPool<byte>.Shared.Rent(BufferSize);
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
                    throw new InvalidOperationException("Decompression failed: the input is not valid Zstandard data.");

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
            if (buffer != null && mode == CompressionMode.Compress)
            {
                try
                {
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
            if (buffer != null && mode == CompressionMode.Compress)
            {
                try
                {
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

