//using System.Buffers;
//using System.IO.Compression;
//using System.Runtime.InteropServices;
//using static NativeCompressions.ZStandard.ZStdNativeMethods;

//namespace NativeCompressions.ZStandard
//{
//    public sealed class ZStdStream : Stream
//    {
//        // use recommended size of zstd stream
//        internal static readonly int CompressInputLimit = (int)ZSTD_CStreamInSize();
//        internal static readonly int CompressOutputLimit = (int)ZSTD_CStreamOutSize();
//        internal static readonly int DecompressInputLimit = (int)ZSTD_DStreamInSize();
//        internal static readonly int DecompressOutputLimit = (int)ZSTD_DStreamOutSize();

//        ZStdEncoder encoder;
//        ZStdDecoder decoder;
//        CompressionMode mode;
//        Stream stream; // when null, stream is disposed.
//        bool leaveOpen;

//        // for both
//        byte[] inputBuffer;
//        int inputBufferOffset;
//        byte[] outputBuffer;

//        // for decode
//        int inputBufferCount;
//        int outputBufferOffset;
//        int outputBufferCount;
//        bool readComplete;

//        public override bool CanRead => mode == CompressionMode.Decompress && stream != null && stream.CanRead;
//        public override bool CanWrite => mode == CompressionMode.Compress && stream != null && stream.CanWrite;
//        public override bool CanSeek => false;
//        public override long Length => throw new NotSupportedException();
//        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
//        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
//        public override void SetLength(long value) => throw new NotSupportedException();

//        public ZStdStream(Stream stream, CompressionMode mode, bool leaveOpen = false)
//        {
//            this.stream = stream;
//            this.mode = mode;
//            this.leaveOpen = leaveOpen;
//            this.inputBuffer = ArrayPool<byte>.Shared.Rent((mode == CompressionMode.Compress) ? CompressInputLimit : DecompressInputLimit);
//            this.outputBuffer = ArrayPool<byte>.Shared.Rent((mode == CompressionMode.Compress) ? CompressOutputLimit : DecompressOutputLimit);

//            if (mode == CompressionMode.Compress)
//            {
//                this.encoder = new ZStdEncoder();
//            }
//            else
//            {
//                this.decoder = new ZStdDecoder();
//            }
//        }

//        public ZStdStream(Stream stream, CompressionLevel compressionLevel, bool leaveOpen = false)
//            : this(stream, ZStdEncoder.ConvertCompressionLevel(compressionLevel), leaveOpen)
//        {

//        }

//        public ZStdStream(Stream stream, int compressionLevel, bool leaveOpen = false)
//        {
//            this.stream = stream;
//            this.mode = CompressionMode.Compress;
//            this.leaveOpen = leaveOpen;
//            this.inputBuffer = ArrayPool<byte>.Shared.Rent(CompressInputLimit);
//            this.outputBuffer = ArrayPool<byte>.Shared.Rent(CompressOutputLimit);
//            this.encoder = new ZStdEncoder(compressionLevel);
//        }

//        public void SetParameter(int param, int value)
//        {
//            if (mode == CompressionMode.Compress)
//            {
//                encoder.SetParameter(param, value);
//            }
//            else
//            {
//                decoder.SetParameter(param, value);
//            }
//        }

//        #region Encode

//        public override void Write(byte[] buffer, int offset, int count)
//        {
//            WriteCore(new ReadOnlySpan<byte>(buffer, offset, count));
//        }

//        public override void Write(ReadOnlySpan<byte> buffer)
//        {
//            WriteCore(buffer);
//        }

//        public override void WriteByte(byte value)
//        {
//            Span<byte> span = stackalloc byte[value];
//            WriteCore(span);
//        }

//        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
//        {
//            return TaskToAsyncResultShim.Begin(WriteAsync(buffer, offset, count, CancellationToken.None), callback, state);
//        }

//        public override void EndWrite(IAsyncResult asyncResult)
//        {
//            TaskToAsyncResultShim.End(asyncResult);
//        }

//        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
//        {
//            return WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();
//        }

//        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
//        {
//            return cancellationToken.IsCancellationRequested
//                ? ValueTask.FromCanceled(cancellationToken)
//                : WriteCoreAsync(buffer, cancellationToken);
//        }

//        public override void Flush()
//        {
//            ValidateDisposed();

//            if (mode != CompressionMode.Compress)
//            {
//                throw new InvalidOperationException("Write operation must be Compress mode.");
//            }

//            if (inputBufferOffset == 0) return;

//            encoder.Compress(inputBuffer.AsSpan(0, inputBufferOffset), outputBuffer.AsSpan(0, CompressOutputLimit), out var bytesConsumed, out var bytesWritten, EndDirective.Flush);
//            stream.Write(outputBuffer, 0, bytesWritten);
//            inputBufferOffset = 0;
//        }

//        public override async Task FlushAsync(CancellationToken cancellationToken)
//        {
//            ValidateDisposed();

//            if (mode != CompressionMode.Compress)
//            {
//                throw new InvalidOperationException("Write operation must be Compress mode.");
//            }

//            if (inputBufferOffset == 0) return;

//            encoder.Compress(inputBuffer.AsSpan(0, inputBufferOffset), outputBuffer.AsSpan(0, CompressOutputLimit), out var bytesConsumed, out var bytesWritten, EndDirective.Flush);
//            await stream.WriteAsync(outputBuffer.AsMemory(0, bytesWritten)).ConfigureAwait(false);
//            inputBufferOffset = 0;
//        }

//        void WriteCore(ReadOnlySpan<byte> source)
//        {
//            ValidateDisposed();

//            if (mode != CompressionMode.Compress)
//            {
//                throw new InvalidOperationException("Write operation must be Compress mode.");
//            }

//            if (source.Length == 0) return;

//            // copy to buffer
//            if (source.Length + inputBufferOffset < CompressInputLimit)
//            {
//                goto COPY_BUFFER;
//            }

//            // write and flush and stream-write
//            if (inputBufferOffset > 0)
//            {
//                encoder.Compress(inputBuffer.AsSpan(0, inputBufferOffset), outputBuffer, out var bytesConsumed, out var bytesWritten, EndDirective.Flush);
//                stream.Write(outputBuffer, 0, bytesWritten);
//                inputBufferOffset = 0;
//            }

//            // copy or write large source
//            if (source.Length < CompressInputLimit)
//            {
//                goto COPY_BUFFER;
//            }

//            // slice per recommended input
//            while (source.Length > 0)
//            {
//                encoder.Compress(source.Slice(0, Math.Min(CompressInputLimit, source.Length)), outputBuffer.AsSpan(0, CompressOutputLimit), out var bytesConsumed, out var bytesWritten, EndDirective.Flush);
//                stream.Write(outputBuffer, 0, bytesWritten);
//                source = source.Slice(bytesConsumed);
//            }
//            return;

//        COPY_BUFFER:
//            source.CopyTo(inputBuffer.AsSpan(inputBufferOffset));
//            inputBufferOffset += source.Length;
//            return;
//        }

//        async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
//        {
//            ValidateDisposed();

//            if (mode != CompressionMode.Compress)
//            {
//                throw new InvalidOperationException("Write operation must be Compress mode.");
//            }

//            if (source.Length == 0) return;

//            // copy to buffer
//            if (source.Length + inputBufferOffset < CompressInputLimit)
//            {
//                goto COPY_BUFFER;
//            }

//            // write and flush and stream-write
//            if (inputBufferOffset > 0)
//            {
//                encoder.Compress(inputBuffer.AsSpan(0, inputBufferOffset), outputBuffer.AsSpan(0, CompressOutputLimit), out var bytesConsumed, out var bytesWritten, EndDirective.Flush);
//                await stream.WriteAsync(outputBuffer.AsMemory(0, bytesWritten)).ConfigureAwait(false);
//                inputBufferOffset = 0;
//            }

//            // copy or write large source
//            if (source.Length < CompressInputLimit)
//            {
//                goto COPY_BUFFER;
//            }

//            // slice per recommended input
//            while (source.Length > 0)
//            {
//                encoder.Compress(source.Span.Slice(0, Math.Min(CompressInputLimit, source.Length)), outputBuffer.AsSpan(0, CompressOutputLimit), out var bytesConsumed, out var bytesWritten, EndDirective.Flush);
//                await stream.WriteAsync(outputBuffer.AsMemory(0, bytesWritten)).ConfigureAwait(false);
//                source = source.Slice(bytesConsumed);
//            }
//            return;

//        COPY_BUFFER:
//            source.Span.CopyTo(inputBuffer.AsSpan(inputBufferOffset));
//            inputBufferOffset += source.Length;
//            return;
//        }

//        #endregion

//        #region Decode

//        public override int Read(byte[] buffer, int offset, int count)
//        {
//            return ReadCore(new Span<byte>(buffer, offset, count));
//        }

//        public override int ReadByte()
//        {
//            byte b = default;
//            var read = Read(MemoryMarshal.CreateSpan(ref b, 1));
//            return read != 0 ? b : -1;
//        }

//        public override int Read(Span<byte> buffer)
//        {
//            return ReadCore(buffer);
//        }

//        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
//        {
//            return TaskToAsyncResultShim.Begin(ReadAsync(buffer, offset, count, CancellationToken.None), callback, state);
//        }

//        public override int EndRead(IAsyncResult asyncResult)
//        {
//            return TaskToAsyncResultShim.End<int>(asyncResult);
//        }

//        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
//        {
//            return ReadCoreAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
//        }

//        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
//        {
//            return ReadCoreAsync(buffer, cancellationToken);
//        }

//        int ReadCore(Span<byte> dest)
//        {
//            ValidateDisposed();
//            if (mode != CompressionMode.Decompress)
//            {
//                throw new InvalidOperationException("Read operation must be Decompress mode.");
//            }

//            if (dest.Length == 0) return 0;

//            var totalWritten = 0;
//            OperationStatus lastStatus = OperationStatus.DestinationTooSmall;
//        AGAIN:
//            // copy already decompressed data
//            if (outputBufferCount != 0)
//            {
//                if (dest.Length < outputBufferCount)
//                {
//                    outputBuffer.AsSpan(outputBufferOffset, dest.Length).CopyTo(dest);
//                    totalWritten += dest.Length;
//                    outputBufferOffset += dest.Length;
//                    outputBufferCount -= dest.Length;
//                    return totalWritten;
//                }
//                else
//                {
//                    // use all data
//                    outputBuffer.AsSpan(outputBufferOffset, outputBufferCount).CopyTo(dest);
//                    totalWritten += outputBufferCount;
//                    dest = dest.Slice(outputBufferCount);
//                    outputBufferOffset = 0;
//                    outputBufferCount = 0;
//                    if (lastStatus == OperationStatus.Done)
//                    {
//                        return totalWritten;
//                    }
//                }
//            }

//            if (readComplete)
//            {
//                return totalWritten;
//            }
//            FillInputBuffer();
//            var input = inputBuffer.AsSpan(inputBufferOffset, inputBufferCount);
//            var output = outputBuffer.AsSpan(0, DecompressOutputLimit);
//            lastStatus = decoder.Decompress(input, output, out var consumed, out var written);
//            if (lastStatus == OperationStatus.NeedMoreData && readComplete)
//            {
//                throw new InvalidOperationException("Require more data but stream is completed.");
//            }

//            inputBufferOffset += consumed;
//            inputBufferCount -= consumed;
//            outputBufferCount = written;

//            if (outputBufferCount == 0)
//            {
//                throw new InvalidOperationException("No compressed data exists.");
//            }
//            goto AGAIN;
//        }

//        async ValueTask<int> ReadCoreAsync(Memory<byte> dest, CancellationToken cancellationToken)
//        {
//            ValidateDisposed();
//            if (mode != CompressionMode.Decompress)
//            {
//                throw new InvalidOperationException("Read operation must be Decompress mode.");
//            }

//            if (dest.Length == 0) return 0;

//            var totalWritten = 0;
//            OperationStatus lastStatus = OperationStatus.DestinationTooSmall;
//        AGAIN:
//            // copy already decompressed data
//            if (outputBufferCount != 0)
//            {
//                if (dest.Length < outputBufferCount)
//                {
//                    outputBuffer.AsSpan(outputBufferOffset, dest.Length).CopyTo(dest.Span);
//                    totalWritten += dest.Length;
//                    outputBufferOffset += dest.Length;
//                    outputBufferCount -= dest.Length;
//                    return totalWritten;
//                }
//                else
//                {
//                    // use all data
//                    outputBuffer.AsSpan(outputBufferOffset, outputBufferCount).CopyTo(dest.Span);
//                    totalWritten += outputBufferCount;
//                    dest = dest.Slice(outputBufferCount);
//                    outputBufferOffset = 0;
//                    outputBufferCount = 0;
//                    if (lastStatus == OperationStatus.Done)
//                    {
//                        return totalWritten;
//                    }
//                }
//            }

//            if (readComplete)
//            {
//                return totalWritten;
//            }
//            await FillInputBufferAsync(cancellationToken).ConfigureAwait(false);
//            lastStatus = decoder.Decompress(inputBuffer.AsSpan(inputBufferOffset, inputBufferCount), outputBuffer.AsSpan(0, DecompressOutputLimit), out var consumed, out var written);
//            if (lastStatus == OperationStatus.NeedMoreData && readComplete)
//            {
//                throw new InvalidOperationException("Require more data but stream is completed.");
//            }

//            inputBufferOffset += consumed;
//            inputBufferCount -= consumed;
//            outputBufferCount = written;

//            if (outputBufferCount == 0)
//            {
//                throw new InvalidOperationException("No compressed data exists.");
//            }
//            goto AGAIN;
//        }

//        void FillInputBuffer()
//        {
//            if (readComplete) return;

//            var startIndex = inputBufferOffset + inputBufferCount; // already filled
//            var span = inputBuffer.AsSpan(startIndex, DecompressInputLimit - startIndex);
//            while (span.Length != 0)
//            {
//                var read = stream.Read(span);
//                inputBufferCount += read;
//                if (read <= 0)
//                {
//                    readComplete = true;
//                    break;
//                }
//                span = span.Slice(read);
//            }
//        }

//        async ValueTask FillInputBufferAsync(CancellationToken cancellationToken)
//        {
//            if (readComplete) return;

//            var startIndex = inputBufferOffset + inputBufferCount; // already filled
//            var memory = inputBuffer.AsMemory(startIndex, DecompressInputLimit - startIndex);
//            while (memory.Length != 0)
//            {
//                var read = await stream.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
//                inputBufferCount += read;
//                if (read <= 0)
//                {
//                    readComplete = true;
//                    break;
//                }
//                memory = memory.Slice(read);
//            }
//        }

//        #endregion

//        void ValidateDisposed()
//        {
//            InternalUtility.ObjectDisposedExceptionThrowIf(stream is null, this);
//        }

//        // Close will call Dispose(true), End Stream.
//        protected override void Dispose(bool disposing)
//        {
//            if (stream == null) return;

//            try
//            {
//                if (disposing)
//                {
//                    if (mode == CompressionMode.Compress)
//                    {
//                        // write rest buffer and final-block
//                        encoder.Compress(inputBuffer.AsSpan(0, inputBufferOffset), outputBuffer, out var _, out var bytesWritten, isFinalBlock: true);
//                        stream.Write(outputBuffer, 0, bytesWritten);
//                        inputBufferOffset = 0;
//                    }

//                    if (!leaveOpen)
//                    {
//                        stream.Dispose();
//                    }
//                }
//            }
//            finally
//            {
//                stream = null!; // IsDisposed
//                ArrayPool<byte>.Shared.Return(inputBuffer);
//                ArrayPool<byte>.Shared.Return(outputBuffer);

//                // free native resources.
//                encoder.Dispose();
//                decoder.Dispose();
//                base.Dispose(disposing);
//            }
//        }

//        public override async ValueTask DisposeAsync()
//        {
//            if (stream == null) return;

//            try
//            {
//                if (mode == CompressionMode.Compress)
//                {
//                    // write rest buffer and final-block
//                    encoder.Compress(inputBuffer.AsSpan(0, inputBufferOffset), outputBuffer, out var _, out var bytesWritten, isFinalBlock: true);
//                    await stream.WriteAsync(outputBuffer.AsMemory(0, bytesWritten)).ConfigureAwait(false);
//                    inputBufferOffset = 0;
//                }

//                if (!leaveOpen)
//                {
//                    await stream.DisposeAsync().ConfigureAwait(false);
//                }
//            }
//            finally
//            {
//                stream = null!; // IsDisposed
//                ArrayPool<byte>.Shared.Return(inputBuffer);
//                ArrayPool<byte>.Shared.Return(outputBuffer);

//                // free native resources.
//                encoder.Dispose();
//                decoder.Dispose();
//                await base.DisposeAsync().ConfigureAwait(false);
//            }
//        }

//        // use finalizer
//        ~ZStdStream()
//        {
//            Dispose(false);
//        }
//    }
//}
