using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;
using static NativeCompressions.ZStandard.ZStdNativeMethods;

namespace NativeCompressions.ZStandard
{
    public sealed class ZStdStream : Stream
    {
        static int GetRecommendedCompressSizeForInput() => (int)ZSTD_CStreamInSize();
        static int GetRecommendedCompressSizeForOutput() => (int)ZSTD_CStreamOutSize();
        static int GetRecommendedDecompressSizeForInput() => (int)ZSTD_DStreamInSize();
        static int GetRecommendedDecompressSizeForOutput() => (int)ZSTD_DStreamOutSize();

        // use this field instead of inputBuffer.Length
        static readonly int CompressInputLimit = GetRecommendedCompressSizeForInput();

        ZStdEncoder encoder;
        ZStdDecoder decoder;
        CompressionMode mode;
        Stream stream;
        bool leaveOpen;
        byte[] inputBuffer;
        int inputBufferOffset;
        byte[] outputBuffer;

        public override bool CanRead => mode == CompressionMode.Decompress && stream != null && stream.CanRead;
        public override bool CanWrite => mode == CompressionMode.Compress && stream != null && stream.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public ZStdStream(Stream stream, CompressionMode mode, bool leaveOpen = false)
        {
            this.stream = stream;
            this.mode = mode;
            this.leaveOpen = leaveOpen;
            this.inputBuffer = ArrayPool<byte>.Shared.Rent((mode == CompressionMode.Compress) ? GetRecommendedCompressSizeForInput() : GetRecommendedDecompressSizeForInput());
            this.outputBuffer = ArrayPool<byte>.Shared.Rent((mode == CompressionMode.Compress) ? GetRecommendedCompressSizeForOutput() : GetRecommendedDecompressSizeForOutput());

            if (mode == CompressionMode.Compress)
            {
                this.encoder = new ZStdEncoder();
            }
            else
            {
                this.decoder = new ZStdDecoder();
            }
        }

        public ZStdStream(Stream stream, CompressionLevel compressionLevel, bool leaveOpen = false)
            : this(stream, ZStdEncoder.ConvertCompressionLevel(compressionLevel), leaveOpen)
        {

        }

        public ZStdStream(Stream stream, int compressionLevel, bool leaveOpen = false)
        {
            this.stream = stream;
            this.mode = CompressionMode.Compress;
            this.leaveOpen = leaveOpen;
            this.inputBuffer = ArrayPool<byte>.Shared.Rent(GetRecommendedCompressSizeForInput());
            this.outputBuffer = ArrayPool<byte>.Shared.Rent(GetRecommendedCompressSizeForOutput());
            this.encoder = new ZStdEncoder(compressionLevel);
        }


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
            Span<byte> span = stackalloc byte[value];
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

            if (inputBufferOffset == 0) return;

            encoder.Compress(inputBuffer.AsSpan(0, inputBufferOffset), outputBuffer, out var bytesConsumed, out var bytesWritten, EndDirective.Flush);
            stream.Write(outputBuffer, 0, bytesWritten);
            inputBufferOffset = 0;
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (mode != CompressionMode.Compress)
            {
                throw new InvalidOperationException("Write operation must be Compress mode.");
            }

            if (inputBufferOffset == 0) return;

            encoder.Compress(inputBuffer.AsSpan(0, inputBufferOffset), outputBuffer, out var bytesConsumed, out var bytesWritten, EndDirective.Flush);
            await stream.WriteAsync(outputBuffer.AsMemory(0, bytesWritten)).ConfigureAwait(false);
            inputBufferOffset = 0;
        }

        void WriteCore(ReadOnlySpan<byte> source)
        {
            if (mode != CompressionMode.Compress)
            {
                throw new InvalidOperationException("Write operation must be Compress mode.");
            }

            if (source.Length == 0) return;

            // copy to buffer
            if (source.Length + inputBufferOffset < CompressInputLimit)
            {
                goto COPY_BUFFER;
            }

            // write and flush and stream-write
            if (inputBufferOffset > 0)
            {
                encoder.Compress(inputBuffer.AsSpan(0, inputBufferOffset), outputBuffer, out var bytesConsumed, out var bytesWritten, EndDirective.Flush);
                stream.Write(outputBuffer, 0, bytesWritten);
                inputBufferOffset = 0;
            }

            // copy or write large source
            if (source.Length < CompressInputLimit)
            {
                goto COPY_BUFFER;
            }

            // slice per recommended input
            while (source.Length > 0)
            {
                encoder.Compress(source, outputBuffer, out var bytesConsumed, out var bytesWritten, EndDirective.Flush);
                stream.Write(outputBuffer, 0, bytesWritten);
                source = source.Slice(bytesConsumed);
            }
            return;

        COPY_BUFFER:
            source.CopyTo(inputBuffer.AsSpan(inputBufferOffset));
            inputBufferOffset += source.Length;
            return;
        }

        async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            if (mode != CompressionMode.Compress)
            {
                throw new InvalidOperationException("Write operation must be Compress mode.");
            }

            if (source.Length == 0) return;

            // copy to buffer
            if (source.Length + inputBufferOffset < CompressInputLimit)
            {
                goto COPY_BUFFER;
            }

            // write and flush and stream-write
            if (inputBufferOffset > 0)
            {
                encoder.Compress(inputBuffer.AsSpan(0, inputBufferOffset), outputBuffer, out var bytesConsumed, out var bytesWritten, EndDirective.Flush);
                await stream.WriteAsync(outputBuffer.AsMemory(0, bytesWritten)).ConfigureAwait(false);
                inputBufferOffset = 0;
            }

            // copy or write large source
            if (source.Length < CompressInputLimit)
            {
                goto COPY_BUFFER;
            }

            // slice per recommended input
            while (source.Length > 0)
            {
                encoder.Compress(source.Span, outputBuffer, out var bytesConsumed, out var bytesWritten, EndDirective.Flush);
                await stream.WriteAsync(outputBuffer.AsMemory(0, bytesWritten)).ConfigureAwait(false);
                source = source.Slice(bytesConsumed);
            }
            return;

        COPY_BUFFER:
            source.Span.CopyTo(inputBuffer.AsSpan(inputBufferOffset));
            inputBufferOffset += source.Length;
            return;
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

        int ReadCore(Span<byte> dest)
        {
            // TODO:
            throw new NotImplementedException();
            //int bytesWritten;
            //while (!TryDecompress(dest, out bytesWritten))
            //{
            //    int bytesRead = stream.Read(buffer, bufferCount, buffer.Length - bufferCount);
            //    if (bytesRead <= 0)
            //    {
            //        break;
            //    }

            //    bufferCount += bytesRead;

            //    if (bufferCount > buffer.Length)
            //    {
            //        throw new InvalidDataException("Invalid data.");
            //    }
            //}

            //return bytesWritten;
        }

        async ValueTask<int> ReadCoreAsync(Memory<byte> dest, CancellationToken cancellationToken)
        {
            // TODO:
            throw new NotImplementedException();
            //int bytesWritten;
            //while (!TryDecompress(dest.Span, out bytesWritten))
            //{
            //    int bytesRead = await stream.ReadAsync(buffer.AsMemory(bufferCount, buffer.Length - bufferCount), cancellationToken).ConfigureAwait(false);
            //    if (bytesRead <= 0)
            //    {
            //        break;
            //    }

            //    bufferCount += bytesRead;

            //    if (bufferCount > buffer.Length)
            //    {
            //        throw new InvalidDataException("Invalid data.");
            //    }
            //}

            //return bytesWritten;
        }


        bool TryDecompress(Span<byte> destination, out int bytesWritten)
        {
            // TODO:
            throw new NotImplementedException();
            //OperationStatus lastResult = decoder.Decompress(new ReadOnlySpan<byte>(buffer, bufferOffset, bufferCount), destination, out int bytesConsumed, out bytesWritten);
            //if (lastResult == OperationStatus.InvalidData)
            //{
            //    throw new InvalidOperationException("Decompress failed, Invalid data.");
            //}

            //if (bytesConsumed != 0)
            //{
            //    bufferOffset += bytesConsumed;
            //    bufferCount -= bytesConsumed;
            //}

            //if (bytesWritten != 0 || lastResult == OperationStatus.Done)
            //{
            //    return true;
            //}

            //if (destination.IsEmpty)
            //{
            //    if (bufferCount != 0)
            //    {
            //        Debug.Assert(bytesWritten == 0);
            //        return true;
            //    }
            //}

            //if (bufferCount != 0 && bufferOffset != 0)
            //{
            //    new ReadOnlySpan<byte>(buffer, bufferOffset, bufferCount).CopyTo(buffer);
            //}
            //bufferOffset = 0;

            //return false;
        }


        #endregion

        // Close will call Dispose(true), End Stream.
        protected override void Dispose(bool disposing)
        {
            // TODO: IsDisposed check.
            try
            {
                if (mode == CompressionMode.Compress)
                {
                    // write rest buffer and final-block
                    encoder.Compress(inputBuffer.AsSpan(0, inputBufferOffset), outputBuffer, out var _, out var bytesWritten, isFinalBlock: true);
                    stream.Write(outputBuffer, 0, bytesWritten);
                    inputBufferOffset = 0;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(inputBuffer);
                ArrayPool<byte>.Shared.Return(outputBuffer);

                // free native resources.
                encoder.Dispose();
                decoder.Dispose();
                base.Dispose(disposing);
            }
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                if (mode == CompressionMode.Compress)
                {
                    // write rest buffer and final-block
                    encoder.Compress(inputBuffer.AsSpan(0, inputBufferOffset), outputBuffer, out var _, out var bytesWritten, isFinalBlock: true);
                    await stream.WriteAsync(outputBuffer.AsMemory(0, bytesWritten)).ConfigureAwait(false);
                    inputBufferOffset = 0;
                }

            }
            finally
            {
                ArrayPool<byte>.Shared.Return(inputBuffer);
                ArrayPool<byte>.Shared.Return(outputBuffer);

                // free native resources.
                encoder.Dispose();
                decoder.Dispose();
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
