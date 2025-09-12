//using System.Buffers;
//using System.IO.Compression;

//namespace NativeCompressions.ZStandard;

///// <summary>
///// Provides a Stream wrapper for ZStandard compression and decompression.
///// </summary>
//public class ZStandardStream : Stream
//{
//    readonly Stream baseStream;
//    readonly CompressionMode mode;
//    readonly bool leaveOpen;
//    readonly ZStandardCompressionOptions options;
//    readonly ZStandardCompressionDictionary? dictionary;
    
//    ZStandardEncoder encoder;
//    ZStandardDecoder decoder;
    
//    byte[]? buffer;
//    int bufferPos;
//    int bufferCount;
    
//    bool disposed;

//    /// <summary>
//    /// Initializes a new instance of the ZStandardStream class using the specified stream and compression mode.
//    /// </summary>
//    public ZStandardStream(Stream stream, CompressionMode mode)
//        : this(stream, mode, leaveOpen: false)
//    {
//    }

//    /// <summary>
//    /// Initializes a new instance of the ZStandardStream class using the specified stream, compression mode, and optionally leaves the stream open.
//    /// </summary>
//    public ZStandardStream(Stream stream, CompressionMode mode, bool leaveOpen)
//        : this(stream, mode, ZStandardCompressionOptions.Default, null, leaveOpen)
//    {
//    }

//    /// <summary>
//    /// Initializes a new instance of the ZStandardStream class with custom options.
//    /// </summary>
//    public ZStandardStream(Stream stream, CompressionMode mode, ZStandardCompressionOptions options, ZStandardCompressionDictionary? dictionary = null, bool leaveOpen = false)
//    {
//        this.baseStream = stream ?? throw new ArgumentNullException(nameof(stream));
//        this.mode = mode;
//        this.leaveOpen = leaveOpen;
//        this.options = options;
//        this.dictionary = dictionary;
        
//        if (mode == CompressionMode.Compress)
//        {
//            if (!stream.CanWrite)
//                throw new ArgumentException("Stream must be writable for compression", nameof(stream));
            
//            encoder = new ZStandardEncoder(options, dictionary);
//        }
//        else
//        {
//            if (!stream.CanRead)
//                throw new ArgumentException("Stream must be readable for decompression", nameof(stream));
            
//            decoder = new ZStandardDecoder(dictionary);
//            buffer = ArrayPool<byte>.Shared.Rent(65536);
//        }
//    }

//    public override bool CanRead => mode == CompressionMode.Decompress && baseStream.CanRead;
//    public override bool CanWrite => mode == CompressionMode.Compress && baseStream.CanWrite;
//    public override bool CanSeek => false;
//    public override long Length => throw new NotSupportedException();
//    public override long Position 
//    { 
//        get => throw new NotSupportedException(); 
//        set => throw new NotSupportedException(); 
//    }

//    public override void Flush()
//    {
//        if (mode == CompressionMode.Compress)
//        {
//            var buffer = ArrayPool<byte>.Shared.Rent(65536);
//            try
//            {
//                var written = encoder.Flush(buffer);
//                if (written > 0)
//                {
//                    baseStream.Write(buffer, 0, written);
//                }
//            }
//            finally
//            {
//                ArrayPool<byte>.Shared.Return(buffer);
//            }
//        }
//        baseStream.Flush();
//    }

//    public override async Task FlushAsync(CancellationToken cancellationToken)
//    {
//        if (mode == CompressionMode.Compress)
//        {
//            var buffer = ArrayPool<byte>.Shared.Rent(65536);
//            try
//            {
//                var written = encoder.Flush(buffer);
//                if (written > 0)
//                {
//                    await baseStream.WriteAsync(buffer, 0, written, cancellationToken);
//                }
//            }
//            finally
//            {
//                ArrayPool<byte>.Shared.Return(buffer);
//            }
//        }
//        await baseStream.FlushAsync(cancellationToken);
//    }

//    public override int Read(byte[] buffer, int offset, int count)
//    {
//        ValidateArguments(buffer, offset, count);
        
//        if (mode != CompressionMode.Decompress)
//            throw new InvalidOperationException("Cannot read from a compression stream");
        
//        return ReadCore(buffer.AsSpan(offset, count));
//    }

//    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
//    {
//        if (mode != CompressionMode.Decompress)
//            throw new InvalidOperationException("Cannot read from a compression stream");
        
//        // Ensure we have data in our internal buffer
//        if (bufferPos >= bufferCount)
//        {
//            bufferPos = 0;
//            bufferCount = await baseStream.ReadAsync(this.buffer!, 0, this.buffer!.Length, cancellationToken);
//            if (bufferCount == 0)
//                return 0;
//        }
        
//        var status = decoder.Decompress(
//            this.buffer.AsSpan(bufferPos, bufferCount - bufferPos),
//            buffer.Span,
//            out int bytesConsumed,
//            out int bytesWritten);
        
//        bufferPos += bytesConsumed;
//        return bytesWritten;
//    }

//    private int ReadCore(Span<byte> buffer)
//    {
//        // Ensure we have data in our internal buffer
//        if (bufferPos >= bufferCount)
//        {
//            bufferPos = 0;
//            bufferCount = baseStream.Read(this.buffer!, 0, this.buffer!.Length);
//            if (bufferCount == 0)
//                return 0;
//        }
        
//        var status = decoder.Decompress(
//            this.buffer.AsSpan(bufferPos, bufferCount - bufferPos),
//            buffer,
//            out int bytesConsumed,
//            out int bytesWritten);
        
//        bufferPos += bytesConsumed;
//        return bytesWritten;
//    }

//    public override void Write(byte[] buffer, int offset, int count)
//    {
//        ValidateArguments(buffer, offset, count);
        
//        if (mode != CompressionMode.Compress)
//            throw new InvalidOperationException("Cannot write to a decompression stream");
        
//        WriteCore(buffer.AsSpan(offset, count));
//    }

//    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
//    {
//        if (mode != CompressionMode.Compress)
//            throw new InvalidOperationException("Cannot write to a decompression stream");
        
//        var tempBuffer = ArrayPool<byte>.Shared.Rent(encoder.GetMaxCompressedLength(buffer.Length));
//        try
//        {
//            var written = encoder.Compress(buffer.Span, tempBuffer);
//            if (written > 0)
//            {
//                await baseStream.WriteAsync(tempBuffer, 0, written, cancellationToken);
//            }
//        }
//        finally
//        {
//            ArrayPool<byte>.Shared.Return(tempBuffer);
//        }
//    }

//    private void WriteCore(ReadOnlySpan<byte> buffer)
//    {
//        var tempBuffer = ArrayPool<byte>.Shared.Rent(encoder.GetMaxCompressedLength(buffer.Length));
//        try
//        {
//            var written = encoder.Compress(buffer, tempBuffer);
//            if (written > 0)
//            {
//                baseStream.Write(tempBuffer, 0, written);
//            }
//        }
//        finally
//        {
//            ArrayPool<byte>.Shared.Return(tempBuffer);
//        }
//    }

//    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
//    public override void SetLength(long value) => throw new NotSupportedException();

//    protected override void Dispose(bool disposing)
//    {
//        if (!disposed)
//        {
//            if (disposing)
//            {
//                if (mode == CompressionMode.Compress)
//                {
//                    // Write frame footer
//                    var tempBuffer = ArrayPool<byte>.Shared.Rent(encoder.GetMaxFlushBufferLength(includingFooter: true));
//                    try
//                    {
//                        var written = encoder.Close(tempBuffer);
//                        if (written > 0)
//                        {
//                            baseStream.Write(tempBuffer, 0, written);
//                        }
//                    }
//                    finally
//                    {
//                        ArrayPool<byte>.Shared.Return(tempBuffer);
//                    }
                    
//                    encoder.Dispose();
//                }
//                else
//                {
//                    decoder.Dispose();
//                    if (buffer != null)
//                    {
//                        ArrayPool<byte>.Shared.Return(buffer);
//                        buffer = null;
//                    }
//                }
                
//                if (!leaveOpen)
//                {
//                    baseStream.Dispose();
//                }
//            }
            
//            disposed = true;
//        }
        
//        base.Dispose(disposing);
//    }

//    private static void ValidateArguments(byte[] buffer, int offset, int count)
//    {
//        if (buffer == null)
//            throw new ArgumentNullException(nameof(buffer));
//        if (offset < 0)
//            throw new ArgumentOutOfRangeException(nameof(offset));
//        if (count < 0)
//            throw new ArgumentOutOfRangeException(nameof(count));
//        if (offset + count > buffer.Length)
//            throw new ArgumentException("Buffer too small");
//    }
//}
