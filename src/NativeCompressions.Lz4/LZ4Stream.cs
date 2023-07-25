using System.IO.Compression;

namespace NativeCompressions.Lz4
{
    public sealed class LZ4Stream : Stream
    {
        LZ4Encoder encoder;
        LZ4Decoder decoder;
        CompressionMode mode;

        Stream stream;
        bool leaveOpen;

        public LZ4Stream(Stream stream, CompressionMode mode, bool leaveOpen = false)
        {
            this.mode = mode;
            this.stream = stream;
            this.leaveOpen = leaveOpen;

            if (mode == CompressionMode.Decompress)
            {
                this.encoder = new LZ4Encoder();
            }
            else
            {
                this.decoder = new LZ4Decoder();
            }
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
            throw new NotImplementedException();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            throw new NotImplementedException();
        }

        public override void WriteByte(byte value)
        {
            throw new NotImplementedException();
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            return base.BeginWrite(buffer, offset, count, callback, state);
        }

        public override void EndWrite(IAsyncResult asyncResult)
        {
            base.EndWrite(asyncResult);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }


        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return base.FlushAsync(cancellationToken);
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

        #endregion

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (mode == CompressionMode.Decompress)
                {
                    // TODO: Flush?
                }
                else
                {
                }
            }
            finally
            {
                encoder.Dispose();
                decoder.Dispose();
                base.Dispose(disposing);
            }
        }

        public override ValueTask DisposeAsync()
        {
            return base.DisposeAsync();
        }
    }
}
