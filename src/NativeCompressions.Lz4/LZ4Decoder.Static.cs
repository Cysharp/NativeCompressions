using System.Buffers;
using System.Runtime.InteropServices;
using static NativeCompressions.Lz4.Lz4NativeMethods;

namespace NativeCompressions.Lz4
{
    public unsafe partial struct LZ4Decoder : IDisposable
    {
        public static string Version => LZ4Encoder.Version;

        public static uint VersionNumber => LZ4Encoder.VersionNumber;

        public static uint FrameVersion => LZ4Encoder.FrameVersion;

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

        public static unsafe bool TryDecompressFrame(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
        {
            using var decoder = new LZ4Decoder();
            return decoder.Decompress(source, destination, out var bytesConsumed, out bytesWritten) == OperationStatus.Done;
        }

        static unsafe string GetErrorName(nuint code)
        {
            var name = (sbyte*)LZ4F_getErrorName(code);
            return new string(name);
        }

        static void HandleError(nuint code)
        {
            if (LZ4F_isError(code) != 0)
            {
                var error = GetErrorName(code);
                throw new InvalidOperationException(error);
            }
        }
    }
}
