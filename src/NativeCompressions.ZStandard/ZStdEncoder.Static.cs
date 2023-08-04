using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static NativeCompressions.ZStandard.ZStdNativeMethods;

namespace NativeCompressions.ZStandard
{
    public unsafe partial struct ZStdEncoder
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
                        version = new string((sbyte*)ZSTD_versionString());
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
                        versionNumber = (uint)ZSTD_versionNumber();
                    }
                }
                return versionNumber.Value;
            }
        }

        public const int DefaultCompressionLevel = 3;    // ZSTD_defaultCLevel
        public const int MinCompressionLevel = -131072;  // ZSTD_minCLevel
        public const int MaxCompressionLevel = 22;       // ZSTD_maxCLevel

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetMaxCompressedLength(int inputSize)
        {
            // C# inlined for performance reason.

            // #define ZSTD_COMPRESSBOUND(srcSize)   ((srcSize) + ((srcSize)>>8) + (((srcSize) < (128<<10)) ? (((128<<10) - (srcSize)) >> 11) /* margin, from 64 to 0 */ : 0))
            // /* this formula ensures that bound(A) + bound(B) <= bound(A+B) as long as A and B >= 128 KB */
            return inputSize + ((inputSize) >> 8) + ((inputSize < (128 << 10)) ? (((128 << 10) - inputSize) >> 11) : 0);
        }

        // TODO: compression level(3 is ZSTD_defaultCLevel, max is 22, min is -131072)
        public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, int compressionLevel = DefaultCompressionLevel)
        {
            unsafe
            {
                fixed (byte* src = &MemoryMarshal.GetReference(source))
                fixed (byte* dest = &MemoryMarshal.GetReference(destination))
                {
                    // @return : compressed size written into `dst` (&lt;= `dstCapacity),
                    // or an error code if it fails (which can be tested using ZSTD_isError()).

                    // TODO:test destination too small???
                    var codeOrWritten = ZSTD_compress(dest, (nuint)destination.Length, src, (nuint)source.Length, compressionLevel);
                    if (IsError(codeOrWritten))
                    {
                        // TODO: GetErrorName(codeOrWritten);
                        bytesWritten = 0;
                        return false;
                    }

                    bytesWritten = (int)codeOrWritten;
                    return true;
                }
            }
        }

        // TODO: move to decoder
        public static unsafe bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
        {
            fixed (byte* src = source)
            fixed (byte* dest = destination)
            {
                // @return : the number of bytes decompressed into `dst` (&lt;= `dstCapacity`),
                // or an errorCode if it fails (which can be tested using ZSTD_isError()).
                var codeOrWritten = ZSTD_decompress(dest, (nuint)destination.Length, src, (nuint)source.Length);
                if (IsError(codeOrWritten))
                {
                    bytesWritten = 0;
                    return false;
                }

                bytesWritten = (int)codeOrWritten;
                return true;
            }
        }


        // TODO: move to decoder
        public static unsafe byte[] Decompress(ReadOnlySpan<byte> source)
        {
            fixed (byte* src = source)
            {
                // TODO:check multiple frame??? test stream mode(is unknown?)
                const ulong ZSTD_CONTENTSIZE_UNKNOWN = unchecked(0UL - 1);
                const ulong ZSTD_CONTENTSIZE_ERROR = unchecked(0UL - 2);
                var size = ZSTD_getFrameContentSize(src, (nuint)source.Length);
                if (size == ZSTD_CONTENTSIZE_UNKNOWN)
                {
                    // TODO:throw
                }
                else if (size == ZSTD_CONTENTSIZE_ERROR)
                {
                    // TODO:throw
                }


                var destination = new byte[checked((int)size)];
                fixed (byte* dest = destination)
                {
                    // @return : the number of bytes decompressed into `dst` (&lt;= `dstCapacity`),
                    // or an errorCode if it fails (which can be tested using ZSTD_isError()).
                    var codeOrWritten = ZSTD_decompress(dest, (nuint)destination.Length, src, (nuint)source.Length);
                    

                    if (IsError(codeOrWritten))
                    {
                        var error = GetErrorName(codeOrWritten);
                        throw new InvalidOperationException(error);
                    }

                    return destination;
                }
            }
        }

        static bool IsError(nuint code)
        {
            return ZSTD_isError(code) != 0;
        }

        static unsafe string GetErrorName(nuint code)
        {
            var name = (sbyte*)ZSTD_getErrorName(code);
            return new string(name);
        }

        static void HandleError(nuint code)
        {
            if (ZSTD_isError(code) != 0)
            {
                var error = GetErrorName(code);
                throw new InvalidOperationException(error);
            }
        }
    }
}
