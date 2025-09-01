using NativeCompressions.LZ4.Raw;
using static NativeCompressions.LZ4.Raw.NativeMethods;

namespace NativeCompressions.LZ4;

public static partial class LZ4
{
    public static class Block
    {
        public static int GetMaxCompressedLength(int inputSize)
        {
            return LZ4_compressBound(inputSize);
        }

        public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            fixed (byte* src = source)
            fixed (byte* dest = destination)
            {
                return LZ4_compress_default(src, dest, source.Length, destination.Length);
            }
        }

        public static unsafe int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, LZ4CompressionDictionary? dictionary = null)
        {
            fixed (byte* src = source)
            fixed (byte* dest = destination)
            {
                int nb;
                if (dictionary == null)
                {
                    // @compressedSize : is the exact complete size of the compressed block.
                    // @return : the number of bytes decompressed into destination buffer
                    nb = LZ4_decompress_safe(src, dest, source.Length, destination.Length);
                }
                else
                {
                    fixed (byte* dict = dictionary.RawDictionary)
                    {
                        nb = LZ4_decompress_safe_usingDict(src, dest, source.Length, destination.Length, dict, dictionary.RawDictionary.Length);
                    }
                }

                if (nb < 0)
                {
                    throw new LZ4Exception("destination buffer is not large enough.");
                }
                return nb;
            }
        }
    }
}
