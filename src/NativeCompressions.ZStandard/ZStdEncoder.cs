using System;
using System.Collections.Generic;
using System.Linq;
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

        // TODO: compression level
        public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, int compressionLevel = 1)
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
            fixed (byte* src = &MemoryMarshal.GetReference(source))
            fixed (byte* dest = &MemoryMarshal.GetReference(destination))
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
