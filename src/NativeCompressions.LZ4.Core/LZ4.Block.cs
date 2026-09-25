using NativeCompressions.Interop;
using static NativeCompressions.Interop.LZ4NativeMethods;

namespace NativeCompressions;

public static partial class LZ4
{
    /// <summary>
    /// The raw LZ4 block format, a single compressed block without frame header, block boundaries or checksums.
    /// The caller has to store the original size, because the block itself does not record it.
    /// </summary>
    public static class Block
    {
        /// <summary>
        /// Gets the maximum size of a compressed block for the given input size.
        /// </summary>
        public static int GetMaxCompressedLength(int inputSize)
        {
            return LZ4_compressBound(inputSize);
        }

        /// <summary>
        /// Compresses the source into a single LZ4 block.
        /// </summary>
        /// <param name="source">The data to compress.</param>
        /// <param name="destination">The buffer to write the block to. Must be at least <see cref="GetMaxCompressedLength"/> bytes.</param>
        /// <returns>The number of bytes written, or 0 when the destination is too small.</returns>
        public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            fixed (byte* src = source)
            fixed (byte* dest = destination)
            {
                return LZ4_compress_default(src, dest, source.Length, destination.Length);
            }
        }

        /// <summary>
        /// Decompresses a single LZ4 block.
        /// </summary>
        /// <param name="source">The complete compressed block.</param>
        /// <param name="destination">The buffer to write the original data to. It must be large enough for the whole block.</param>
        /// <param name="dictionary">The dictionary the block was compressed with, or null.</param>
        /// <returns>The number of bytes written to the destination.</returns>
        /// <exception cref="LZ4Exception">Thrown when the block is invalid or the destination is too small.</exception>
        public static unsafe int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, LZ4Dictionary? dictionary = null)
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
                    nb = LZ4_decompress_safe_usingDict(src, dest, source.Length, destination.Length, dictionary.RawDictionaryPointer, dictionary.RawDictionaryLength);
                    GC.KeepAlive(dictionary); // the pointer stays valid only while the dictionary is alive
                }

                if (nb < 0)
                {
                    throw new LZ4Exception("Invalid LZ4 block, or the destination buffer is too small.");
                }
                return nb;
            }
        }
    }
}
