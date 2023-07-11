using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static NativeCompressions.Lz4.Lz4NativeMethods;

namespace NativeCompressions.Lz4
{
    // https://github.com/lz4/lz4/blob/v1.9.4/lib/lz4.h

    public unsafe partial struct LZ4Encoder : IDisposable
    {
        LZ4_stream_u* stream;

        public LZ4Encoder()
        {
            this.stream = LZ4_createStream();
        }


        //public void Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)
        //{

        //    fixed (byte* src = &MemoryMarshal.GetReference(source))
        //    fixed (byte* dest = &MemoryMarshal.GetReference(destination))
        //    {
        //        LZ4_compress_fast_continue(stream, src, dest, source.Length, destination.Length, acceleration: 1);


        //        //var size = LZ4_decompress_safe_partial(src, dest, source.Length, targetOutputSize, destination.Length);
        //        //if (size < 0)
        //        //{
        //        //    Throw(); // error.
        //        //}
        //        //return size;
        //    }

        //}


        // public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)
        // public OperationStatus Flush(Span<byte> destination, out int bytesWritten)

        public void Dispose()
        {
            if (stream != null)
            {
                var foo = LZ4_freeStream(stream); // return value???
                stream = null;
            }
        }
    }
}
