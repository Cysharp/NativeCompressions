using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static NativeCompressions.Lz4.Lz4NativeMethods;

namespace NativeCompressions.Lz4
{
    public unsafe partial struct LZ4Decoder : IDisposable
    {
        LZ4_streamDecode_u* stream;
        byte* dict;
        int MaxDictSize = 65536;

        public LZ4Decoder()
        {
            stream = LZ4_createStreamDecode();
            dict = (byte*)NativeMemory.Alloc((nuint)MaxDictSize);
        }

        public int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            fixed (byte* src = &MemoryMarshal.GetReference(source))
            fixed (byte* dest = &MemoryMarshal.GetReference(destination))
            {
                var ret = LZ4_decompress_safe_usingDict(src, dest, source.Length, destination.Length, dict, MaxDictSize);

                // var ret = LZ4_decompress_safe_continue(stream, src, dest, source.Length, destination.Length);

                return ret;
            }
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }
}
