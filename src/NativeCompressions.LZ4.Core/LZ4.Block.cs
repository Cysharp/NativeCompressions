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
        const int MaxInputSize = 0x7E000000; // LZ4_MAX_INPUT_SIZE, the largest input a single block can hold

        /// <summary>
        /// Gets the maximum size of a compressed block for the given input size.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when inputSize is negative or larger than 2,113,929,216 (LZ4_MAX_INPUT_SIZE), the largest input a single block can hold.</exception>
        public static int GetMaxCompressedLength(int inputSize)
        {
            // lz4 answers 0 for a size it cannot compress, which reads as a bound of no bytes
            if ((uint)inputSize > MaxInputSize) throw new ArgumentOutOfRangeException(nameof(inputSize));
            return LZ4_compressBound(inputSize);
        }

        /// <summary>
        /// Compresses the source into a single LZ4 block.
        /// </summary>
        /// <param name="source">The data to compress, at most 2,113,929,216 bytes.</param>
        /// <param name="destination">The buffer to write the block to. Must be at least <see cref="GetMaxCompressedLength"/> bytes.</param>
        /// <returns>The number of bytes written, or 0 when the destination is too small or the source is larger than that.</returns>
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
                    using var lease = dictionary.Acquire();
                    nb = LZ4_decompress_safe_usingDict(src, dest, source.Length, destination.Length, lease.Data, lease.DataLength);
                }

                if (nb < 0)
                {
                    // lz4 tells these apart since v1.11.0, any other negative value is an error as well
                    throw new LZ4Exception(nb switch
                    {
                        -2 => "The destination buffer is too small for the LZ4 block, or the block is corrupted.",
                        -3 => "The LZ4 block refers to data before the start of its output. The dictionary is missing or wrong, or the block is corrupted.",
                        _ => "Invalid LZ4 block.",
                    });
                }
                return nb;
            }
        }
    }
}
