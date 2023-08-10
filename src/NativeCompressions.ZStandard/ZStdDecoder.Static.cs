using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static NativeCompressions.ZStandard.ZStdNativeMethods;

namespace NativeCompressions.ZStandard
{
    public unsafe partial struct ZStdDecoder
    {
        // TODO: move to decoder
        public static unsafe bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
        {
            fixed (byte* src = source)
            fixed (byte* dest = destination)
            {
                // @return : the number of bytes decompressed into `dst` (&lt;= `dstCapacity`),
                // or an errorCode if it fails (which can be testedvar  using ZSTD_isError()).
                var codeOrWritten = ZSTD_decompress(dest, (nuint)destination.Length, src, (nuint)source.Length);
                if (IsError(codeOrWritten))
                {
                    HandleError(codeOrWritten); // TODO: return false;
                    bytesWritten = 0;
                    return false;
                }

                bytesWritten = (int)codeOrWritten;
                return true;
            }
        }


        public static unsafe byte[] Decompress(ReadOnlySpan<byte> source)
        {
            fixed (byte* src = source)
            {
                const ulong ZSTD_CONTENTSIZE_UNKNOWN = unchecked(0UL - 1);
                const ulong ZSTD_CONTENTSIZE_ERROR = unchecked(0UL - 2);
                var size = ZSTD_getFrameContentSize(src, (nuint)source.Length);
                if (size == ZSTD_CONTENTSIZE_UNKNOWN)
                {
                    throw new InvalidOperationException("Content size is unknown.");
                }
                else if (size == ZSTD_CONTENTSIZE_ERROR)
                {
                    throw new InvalidOperationException("Content size error.");
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
