using System.Runtime.InteropServices;
using static NativeCompressions.Lz4.Lz4NativeMethods;

namespace NativeCompressions.Lz4
{
    // https://github.com/lz4/lz4/blob/v1.9.4/lib/lz4.h
    // https://htmlpreview.github.io/?https://github.com/lz4/lz4/blob/master/doc/lz4_manual.html

    public unsafe partial struct LZ4Encoder : IDisposable
    {
        bool disposed;
        LZ4_stream_u* stream;

        public LZ4Encoder()
        {
            this.stream = LZ4_createStream();
            Reset();
        }

        public void Reset()
        {
            ThrowIfDisposed();
            LZ4_resetStream_fast(stream);
        }

        public int Compress(ReadOnlySpan<byte> source, Span<byte> destination, int acceleration = 1)
        {
            ThrowIfDisposed();

            fixed (byte* src = &MemoryMarshal.GetReference(source))
            fixed (byte* dest = &MemoryMarshal.GetReference(destination))
            {
                var size = LZ4_compress_fast_continue(stream, src, dest, source.Length, destination.Length, acceleration: acceleration);
                if (size < 0)
                {
                    Throw();
                }
                return size;
            }
        }

        void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("");
            }
        }

        public void Dispose()
        {
            if (stream != null)
            {
                LZ4_freeStream(stream);
                stream = null;
                disposed = true;
            }
        }
    }
}
