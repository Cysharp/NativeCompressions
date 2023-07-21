using System.Runtime.InteropServices;
using static NativeCompressions.Lz4.Lz4NativeMethods;

namespace NativeCompressions.Lz4
{
    // https://github.com/lz4/lz4/blob/v1.9.4/lib/lz4.h
    // https://github.com/lz4/lz4/blob/v1.9.4/lib/lz4frame.h

    // spec
    // https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md
    // https://github.com/lz4/lz4/blob/dev/doc/lz4_Frame_format.md

    // manual
    // https://htmlpreview.github.io/?https://github.com/lz4/lz4/blob/master/doc/lz4_manual.html
    // https://htmlpreview.github.io/?https://github.com/lz4/lz4/blob/master/doc/lz4frame_manual.html
    public unsafe partial struct LZ4Encoder : IDisposable
    {
        bool disposed;

        LZ4F_cctx_s* context;

        public LZ4Encoder()
        {
            // @cctxPtr MUST be != NULL.  If @return != zero, context creation failed
            LZ4F_cctx_s* ptr = default;
            var code = LZ4F_createCompressionContext(&ptr, FrameVersion);
            if (code != 0)
            {
                Throw();
            }

            this.context = ptr;
        }

        public void Compress(ReadOnlySpan<byte> source, Span<byte> destination)
        {

            // Begin

            // LZ4F_compressBegin()

            // Update->Update->Update

            // End?


        }

        public void Flush()
        {

        }



        // TODO: trydecompress???
        //public int Compress(ReadOnlySpan<byte> source, Span<byte> destination, int acceleration = 1)
        //{
        //    ThrowIfDisposed();

        //    LZ4F_preferences_t

        //    fixed (byte* src = &MemoryMarshal.GetReference(source))
        //    fixed (byte* dest = &MemoryMarshal.GetReference(destination))
        //    {
        //        var size = LZ4_compress_fast_continue(stream, src, dest, source.Length, destination.Length, acceleration: acceleration);
        //        if (size < 0)
        //        {
        //            Throw();
        //        }

        //        return size;
        //    }
        //}

        void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("");
            }
        }

        public void Dispose()
        {
            //if (stream != null)
            //{
            //    LZ4_freeStream(stream);
            //    NativeMemory.Free(dict);
            //    stream = null;
            //    disposed = true;
            //}
        }
    }
}
