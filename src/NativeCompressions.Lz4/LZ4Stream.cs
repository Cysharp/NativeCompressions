using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace NativeCompressions.Lz4
{
    public sealed class LZ4Stream : Stream
    {
        LZ4Encoder encoder;
        LZ4Decoder decoder;
        CompressionMode mode;

        Stream stream;
        bool leaveOpen;
        byte[] buffer; // both compress and decompress
        int bufferOffset;
        int bufferCount; // use only decompress

        public LZ4Stream(Stream stream, CompressionMode mode, bool leaveOpen = false)
        {
            this.mode = mode;
            this.stream = stream;
            this.leaveOpen = leaveOpen;
            this.buffer = ArrayPool<byte>.Shared.Rent(LZ4Encoder.GetMaxFrameCompressedLength(0, withHeader: false));
            this.bufferOffset = 0;
            this.bufferCount = 0;

            if (mode == CompressionMode.Decompress)
            {
                this.decoder = new LZ4Decoder();
            }
            else
            {
                this.encoder = new LZ4Encoder(null);
            }
        }

        public LZ4Stream(Stream stream, LZ4FrameHeader compressFrameHeader, bool leaveOpen = false)
        {
            this.mode = CompressionMode.Compress;
            this.stream = stream;
            this.leaveOpen = leaveOpen;
            this.buffer = ArrayPool<byte>.Shared.Rent(LZ4Encoder.GetMaxFrameCompressedLength(0, withHeader: false, frameHeader: compressFrameHeader));
            this.bufferOffset = 0;
            this.bufferCount = 0;
            this.encoder = new LZ4Encoder(compressFrameHeader);
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

            if (!encoder.IsWrittenHeader)
            {
                WriteHeaderCore();
            }

            if (bufferOffset != 0)
            {
                stream.Write(buffer, 0, bufferOffset);
                bufferOffset = 0;
            }

            var written = encoder.Flush(buffer);
            stream.Write(buffer, 0, written);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (mode != CompressionMode.Compress)
            {
                throw new InvalidOperationException("Write operation must be Compress mode.");
            }

            if (!encoder.IsWrittenHeader)
            {
                await WriteHeaderCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            if (bufferOffset != 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, bufferOffset), cancellationToken).ConfigureAwait(false);
                bufferOffset = 0;
            }

            var written = encoder.Flush(buffer);
            await stream.WriteAsync(buffer.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
        }

        void WriteHeaderCore()
        {
            Span<byte> dest = stackalloc byte[LZ4Encoder.MaxFrameHeaderLength];
            var written = encoder.WriteHeader(dest);
            stream.Write(dest.Slice(0, written));
        }

        async ValueTask WriteHeaderCoreAsync(CancellationToken cancellationToken)
        {
            var dest = ArrayPool<byte>.Shared.Rent(LZ4Encoder.MaxFrameHeaderLength);
            try
            {
                var written = encoder.WriteHeader(dest);
                await stream.WriteAsync(dest.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(dest);
            }
        }

        void WriteCore(ReadOnlySpan<byte> source)
        {
            if (mode != CompressionMode.Compress)
            {
                throw new InvalidOperationException("Write operation must be Compress mode.");
            }

            if (!encoder.IsWrittenHeader)
            {
                WriteHeaderCore();
            }

            var dest = buffer.AsSpan(bufferOffset);
            var maxDest = LZ4Encoder.GetMaxFrameCompressedLength(source.Length, withHeader: false, frameHeader: encoder.FrameHader);

            if (dest.Length < maxDest)
            {
                stream.Write(buffer, 0, bufferOffset);

                bufferOffset = 0;
                RentBuffer(maxDest);
                dest = buffer.AsSpan();
            }

            var size = encoder.Compress(source, dest);
            bufferOffset += size;
        }

        async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            if (mode != CompressionMode.Compress)
            {
                throw new InvalidOperationException("Write operation must be Compress mode.");
            }

            if (!encoder.IsWrittenHeader)
            {
                WriteHeaderCore();
            }

            var dest = buffer.AsMemory(bufferOffset);
            var maxDest = LZ4Encoder.GetMaxFrameCompressedLength(source.Length, withHeader: false, frameHeader: encoder.FrameHader);

            if (dest.Length < maxDest)
            {
                await stream.WriteAsync(buffer.AsMemory(0, bufferOffset), cancellationToken).ConfigureAwait(false);

                bufferOffset = 0;
                RentBuffer(maxDest);
                dest = buffer.AsMemory();
            }

            var size = encoder.Compress(source.Span, dest.Span);
            bufferOffset += size;
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
            int bytesWritten;
            while (!TryDecompress(dest, out bytesWritten))
            {
                int bytesRead = stream.Read(buffer, bufferCount, buffer.Length - bufferCount);
                if (bytesRead <= 0)
                {
                    break;
                }

                bufferCount += bytesRead;

                if (bufferCount > buffer.Length)
                {
                    throw new InvalidDataException("Invalid data.");
                }
            }

            return bytesWritten;
        }

        async ValueTask<int> ReadCoreAsync(Memory<byte> dest, CancellationToken cancellationToken)
        {
            int bytesWritten;
            while (!TryDecompress(dest.Span, out bytesWritten))
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(bufferCount, buffer.Length - bufferCount), cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    break;
                }

                bufferCount += bytesRead;

                if (bufferCount > buffer.Length)
                {
                    throw new InvalidDataException("Invalid data.");
                }
            }

            return bytesWritten;
        }


        bool TryDecompress(Span<byte> destination, out int bytesWritten)
        {
            OperationStatus lastResult = decoder.Decompress(new ReadOnlySpan<byte>(buffer, bufferOffset, bufferCount), destination, out int bytesConsumed, out bytesWritten);
            if (lastResult == OperationStatus.InvalidData)
            {
                throw new InvalidOperationException("Decompress failed, Invalid data.");
            }

            if (bytesConsumed != 0)
            {
                bufferOffset += bytesConsumed;
                bufferCount -= bytesConsumed;
            }

            if (bytesWritten != 0 || lastResult == OperationStatus.Done)
            {
                return true;
            }

            if (destination.IsEmpty)
            {
                if (bufferCount != 0)
                {
                    Debug.Assert(bytesWritten == 0);
                    return true;
                }
            }

            if (bufferCount != 0 && bufferOffset != 0)
            {
                new ReadOnlySpan<byte>(buffer, bufferOffset, bufferCount).CopyTo(buffer);
            }
            bufferOffset = 0;

            return false;
        }


        #endregion

        void RentBuffer(int rentSize)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = ArrayPool<byte>.Shared.Rent(rentSize);
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (mode == CompressionMode.Compress)
                {
                    if (!encoder.IsWrittenFooter)
                    {
                        Flush();
                    }
                }

                ArrayPool<byte>.Shared.Return(buffer);
            }
            finally
            {
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
                    if (!encoder.IsWrittenFooter)
                    {
                        await FlushAsync().ConfigureAwait(false);
                    }
                }

                ArrayPool<byte>.Shared.Return(buffer);
            }
            finally
            {
                encoder.Dispose();
                decoder.Dispose();
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
