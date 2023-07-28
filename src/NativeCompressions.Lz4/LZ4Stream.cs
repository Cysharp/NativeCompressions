using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace NativeCompressions.Lz4
{
    public sealed class LZ4Stream : Stream
    {
        LZ4Encoder encoder;
        LZ4Decoder decoder;
        CompressionMode mode;

        Stream stream;
        bool leaveOpen;
        byte[] buffer;
        int bufferCount;

        public LZ4Stream(Stream stream, CompressionMode mode, bool leaveOpen = false)
        {
            this.mode = mode;
            this.stream = stream;
            this.leaveOpen = leaveOpen;
            this.buffer = ArrayPool<byte>.Shared.Rent(LZ4Encoder.GetMaxFrameCompressedLength(0, withHeader: false));

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

            if (bufferCount != 0)
            {
                stream.Write(buffer, 0, bufferCount);
                bufferCount = 0;
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

            if (bufferCount != 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, bufferCount), cancellationToken).ConfigureAwait(false);
                bufferCount = 0;
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

            var dest = buffer.AsSpan(bufferCount);
            var maxDest = LZ4Encoder.GetMaxFrameCompressedLength(source.Length, withHeader: false, frameHeader: encoder.FrameHader);

            if (dest.Length < maxDest)
            {
                stream.Write(buffer, 0, bufferCount);

                bufferCount = 0;
                RentBuffer(maxDest);
                dest = buffer.AsSpan();
            }

            var size = encoder.Compress(source, dest);
            bufferCount += size;
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

            var dest = buffer.AsMemory(bufferCount);
            var maxDest = LZ4Encoder.GetMaxFrameCompressedLength(source.Length, withHeader: false, frameHeader: encoder.FrameHader);

            if (dest.Length < maxDest)
            {
                await stream.WriteAsync(buffer.AsMemory(0, bufferCount), cancellationToken).ConfigureAwait(false);

                bufferCount = 0;
                RentBuffer(maxDest);
                dest = buffer.AsMemory();
            }

            var size = encoder.Compress(source.Span, dest.Span);
            bufferCount += size;
        }

        #endregion

        #region Decode

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override int ReadByte()
        {
            return base.ReadByte();
        }

        public override int Read(Span<byte> buffer)
        {
            return base.Read(buffer);
        }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            return base.BeginRead(buffer, offset, count, callback, state);
        }

        public override int EndRead(IAsyncResult asyncResult)
        {
            return base.EndRead(asyncResult);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(buffer, cancellationToken);
        }

        int ReadCore(Span<byte> dest)
        {
            // TODO: full consume???

            if (bufferCount == 0)
            {
                bufferCount = stream.Read(buffer.AsSpan());
            }


            var status = decoder.Decompress(buffer, dest, out var consumed, out var written);

            bufferCount += consumed;
            throw new NotImplementedException();
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
