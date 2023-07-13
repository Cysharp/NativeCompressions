using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static NativeCompressions.Lz4.Lz4NativeMethods;

namespace NativeCompressions.Lz4
{
    public partial struct LZ4Encoder
    {
        static string? version;
        static uint? versionNumber;

        public static string Version
        {
            get
            {
                if (version == null)
                {
                    unsafe
                    {
                        // null-terminated
                        version = new string((sbyte*)LZ4_versionString());
                    }
                }
                return version;
            }
        }

        public static uint VersionNumber
        {
            get
            {
                if (versionNumber == null)
                {
                    unsafe
                    {
                        versionNumber = (uint)LZ4_versionNumber();
                    }
                }
                return versionNumber.Value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetMaxCompressedLength(int inputSize)
        {
            // C# inlined for performance reason.

            // #define LZ4_MAX_INPUT_SIZE        0x7E000000   /* 2 113 929 216 bytes */
            // #define LZ4_COMPRESSBOUND(isize)  ((unsigned)(isize) > (unsigned)LZ4_MAX_INPUT_SIZE ? 0 : (isize) + ((isize)/255) + 16)
            const int LZ4_MAX_INPUT_SIZE = 0x7E000000;
            return ((uint)(inputSize) > (uint)LZ4_MAX_INPUT_SIZE ? 0 : (inputSize) + ((inputSize) / 255) + 16);
        }

        /// <summary>
        /// <para>Invoke LZ4_compress_default().</para>
        /// Compresses 'source' and return new buffer.
        /// </summary>
        public static byte[] Compress(ReadOnlySpan<byte> source)
        {
            var destSize = GetMaxCompressedLength(source.Length);

            if (destSize < 512)
            {
                Span<byte> dest = stackalloc byte[destSize];
                if (!TryCompress(source, dest, out var bytesWritten))
                {
                    Throw();
                }
                return dest.FastToArray(bytesWritten);
            }
            else
            {
                var dest = ArrayPool<byte>.Shared.Rent(destSize);
                try
                {
                    if (!TryCompress(source, dest, out var bytesWritten))
                    {
                        Throw();
                    }
                    return dest.FastToArray(bytesWritten);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(dest);
                }
            }
        }

        /// <summary>
        /// <para>Invoke LZ4_compress_default().</para>
        /// Compresses 'source' into already allocated 'destination' buffer.  Compression is guaranteed to succeed if 'destination.Length' &gt;= LZ4_compressBound(source.Length).
        /// If the function cannot compress 'source' into a more limited 'destination' budget, compression stops *immediately*. In which case, 'destination' content is undefined (invalid).
        /// </summary>
        public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
        {
            unsafe
            {
                fixed (byte* src = &MemoryMarshal.GetReference(source))
                fixed (byte* dest = &MemoryMarshal.GetReference(destination))
                {
                    bytesWritten = LZ4_compress_default(src, dest, source.Length, destination.Length);
                    if (bytesWritten == 0)
                    {
                        return false;
                    }

                    return true;
                }
            }
        }

        /// <summary>
        /// <para>Invoke LZ4_compress_fast().</para>
        /// Same as LZ4_compress_default(), but allows selection of "acceleration" factor.The larger the acceleration value, the faster the algorithm, but also the lesser the compression.It's a trade-off.
        /// It can be fine tuned, with each successive value providing roughly +~3% to speed.
        /// An acceleration value of "1" is the same as regular LZ4_compress_default()Values &lt;= 0 will be replaced by LZ4_ACCELERATION_DEFAULT (currently == 1, see lz4.c).
        /// Values &gt; LZ4_ACCELERATION_MAX will be replaced by LZ4_ACCELERATION_MAX (currently == 65537, see lz4.c).
        /// </summary>
        /// <param name="acceleration">1 is the same as regular compress, Max is 65537.</param>
        public static byte[] CompressWithAcceleration(ReadOnlySpan<byte> source, int acceleration)
        {
            var destSize = GetMaxCompressedLength(source.Length);

            if (destSize < 512)
            {
                Span<byte> dest = stackalloc byte[destSize];
                if (!TryCompressWithAcceleration(source, dest, out var bytesWritten, acceleration))
                {
                    Throw();
                }
                return dest.FastToArray(bytesWritten);
            }
            else
            {
                var dest = ArrayPool<byte>.Shared.Rent(destSize);
                try
                {
                    if (!TryCompressWithAcceleration(source, dest, out var bytesWritten, acceleration))
                    {
                        Throw();
                    }
                    return dest.FastToArray(bytesWritten);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(dest);
                }
            }
        }


        /// <summary>
        /// <para>Invoke LZ4_compress_fast().</para>
        /// Same as LZ4_compress_default(), but allows selection of "acceleration" factor.The larger the acceleration value, the faster the algorithm, but also the lesser the compression.It's a trade-off.
        /// It can be fine tuned, with each successive value providing roughly +~3% to speed.
        /// An acceleration value of "1" is the same as regular LZ4_compress_default()Values &lt;= 0 will be replaced by LZ4_ACCELERATION_DEFAULT (currently == 1, see lz4.c).
        /// Values &gt; LZ4_ACCELERATION_MAX will be replaced by LZ4_ACCELERATION_MAX (currently == 65537, see lz4.c).
        /// </summary>
        /// <param name="acceleration">1 is the same as regular compress, Max is 65537.</param>
        public static bool TryCompressWithAcceleration(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, int acceleration)
        {
            unsafe
            {
                fixed (byte* src = &MemoryMarshal.GetReference(source))
                fixed (byte* dest = &MemoryMarshal.GetReference(destination))
                {
                    bytesWritten = LZ4_compress_fast(src, dest, source.Length, destination.Length, acceleration);
                    if (bytesWritten == 0)
                    {
                        return false;
                    }

                    return true;
                }
            }
        }

        // HC

        /// <summary>
        /// <para>Invoke LZ4_compress_HC().</para>
        /// Compress data from `source` and return new buffer, using the powerful but slower "HC" algorithm.
        /// </summary>
        public static byte[] CompressHC(ReadOnlySpan<byte> source, LZ4HCCompressionLevel compressionLevel)
        {
            var destSize = GetMaxCompressedLength(source.Length);

            if (destSize < 512)
            {
                Span<byte> dest = stackalloc byte[destSize];
                if (!TryCompressHC(source, dest, out var bytesWritten, compressionLevel))
                {
                    Throw();
                }
                return dest.FastToArray(bytesWritten);
            }
            else
            {
                var dest = ArrayPool<byte>.Shared.Rent(destSize);
                try
                {
                    if (!TryCompressHC(source, dest, out var bytesWritten, compressionLevel))
                    {
                        Throw();
                    }
                    return dest.FastToArray(bytesWritten);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(dest);
                }
            }
        }

        /// <summary>
        /// <para>Invoke LZ4_compress_HC().</para>
        /// Compress data from `source` into `destination`, using the powerful but slower "HC" algorithm.
        /// </summary>
        public static bool TryCompressHC(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, LZ4HCCompressionLevel compressionLevel)
        {
            unsafe
            {
                fixed (byte* src = &MemoryMarshal.GetReference(source))
                fixed (byte* dest = &MemoryMarshal.GetReference(destination))
                {
                    bytesWritten = LZ4_compress_HC(src, dest, source.Length, destination.Length, (int)compressionLevel);
                    if (bytesWritten == 0)
                    {
                        return false;
                    }

                    return true;
                }
            }
        }

        // Decompress

        /// <summary>
        /// <para>Invoke LZ4_decompress_safe().</para>
        /// If destination buffer is not large enough, decoding will stop and output an error code (negative value).
        /// If the source stream is detected malformed, the function will stop decoding and return a negative result. 
        /// <para>Note 1 : This function is protected against malicious data packets : it will never writes outside 'dst' buffer, nor read outside 'source' buffer,
        /// even if the compressed block is maliciously modified to order the decoder to do these actions.
        /// In such case, the decoder stops immediately, and considers the compressed block malformed.</para>
        /// <para>Note 2 : compressedSize and dstCapacity must be provided to the function, the compressed block does not contain them.          The implementation is free to send / store / derive this information in whichever way is most beneficial.          If there is a need for a different format which bundles together both compressed data and its metadata, consider looking at lz4frame.h instead.</para>
        /// </summary>
        public static unsafe bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
        {
            fixed (byte* src = &MemoryMarshal.GetReference(source))
            fixed (byte* dest = &MemoryMarshal.GetReference(destination))
            {
                bytesWritten = LZ4_decompress_safe(src, dest, source.Length, destination.Length);
                if (bytesWritten < 0)
                {
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// <para>Invoke LZ4_decompress_safe_partial()</para>
        /// Decompress an LZ4 compressed block into destination buffer.
        /// Up to 'targetOutputSize' bytes will be decoded. 
        /// The function stops decoding on reaching this objective.
        /// This can be useful to boost performance  whenever only the beginning of a block is required.
        /// If source stream is detected malformed, function returns a negative result.
        /// <para>Note 1 : @return can be &lt; targetOutputSize, if compressed block contains less data.</para>
        /// <para>Note 2 : targetOutputSize must be &lt;= dstCapacity</para>
        /// <para>Note 3 : this function effectively stops decoding on reaching targetOutputSize, so dstCapacity is kind of redundant. This is because in older versions of this function, decoding operation would still write complete sequences.
        /// Therefore, there was no guarantee that it would stop writing at exactly targetOutputSize, it could write more bytes, though only up to dstCapacity.
        /// Some \"margin\" used to be required for this operation to work properly.
        /// Thankfully, this is no longer necessary.
        /// The function nonetheless keeps the same signature, in an effort to preserve API compatibility.</para>
        /// <para>Note 4 : If srcSize is the exact size of the block, then targetOutputSize can be any value, including larger than the block's decompressed size. The function will, at most, generate block's decompressed size.</para>
        /// <para>Note 5 : If srcSize is _larger_ than block's compressed size, then targetOutputSize **MUST** be &lt;= block's decompressed size. Otherwise, *silent corruption will occur*.</para>
        /// </summary>
        public static unsafe bool TryDecompressPartial(ReadOnlySpan<byte> source, Span<byte> destination, int targetOutputSize, out int bytesWritten)
        {
            fixed (byte* src = &MemoryMarshal.GetReference(source))
            fixed (byte* dest = &MemoryMarshal.GetReference(destination))
            {
                bytesWritten = LZ4_decompress_safe_partial(src, dest, source.Length, targetOutputSize, destination.Length);
                if (bytesWritten < 0)
                {
                    return false;
                }
                return true;
            }
        }

        // Stream API?

        [DoesNotReturn]
        static void Throw()
        {
            // TODO:
            throw new Exception();
        }
    }
}
