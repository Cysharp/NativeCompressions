using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

        public static byte[] Compress(ReadOnlySpan<byte> source, CompressionLevel compressionLevel)
        {
            return Compress(source, ConvertCompressionLevel(compressionLevel));
        }

        public static byte[] Compress(ReadOnlySpan<byte> source, int compressionLevel = DefaultCompressionLevel)
        {
            var destSize = GetMaxCompressedLength(source.Length);
            if (destSize < 512)
            {
                Span<byte> dest = stackalloc byte[destSize];
                if (!TryCompress(source, dest, out var bytesWritten, compressionLevel))
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

        public static void Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, CompressionLevel compressionLevel)
        {
            Compress(source, destination, out bytesWritten, ConvertCompressionLevel(compressionLevel));
        }

        public static void Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, int compressionLevel = DefaultCompressionLevel)
        {
            unsafe
            {
                fixed (byte* src = &MemoryMarshal.GetReference(source))
                fixed (byte* dest = &MemoryMarshal.GetReference(destination))
                {
                    // @return : compressed size written into `dst` (&lt;= `dstCapacity),
                    // or an error code if it fails (which can be tested using ZSTD_isError()).
                    var codeOrWritten = ZSTD_compress(dest, (nuint)destination.Length, src, (nuint)source.Length, compressionLevel);
                    HandleError(codeOrWritten);
                    bytesWritten = (int)codeOrWritten;
                }
            }
        }

        public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, CompressionLevel compressionLevel)
        {
            return TryCompress(source, destination, out bytesWritten, ConvertCompressionLevel(compressionLevel));
        }

        public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, int compressionLevel = DefaultCompressionLevel)
        {
            unsafe
            {
                fixed (byte* src = &MemoryMarshal.GetReference(source))
                fixed (byte* dest = &MemoryMarshal.GetReference(destination))
                {
                    // @return : compressed size written into `dst` (&lt;= `dstCapacity),
                    // or an error code if it fails (which can be tested using ZSTD_isError()).
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

        [DoesNotReturn]
        static void Throw()
        {
            // TODO:
            throw new Exception();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int ConvertCompressionLevel(CompressionLevel compressionLevel)
        {
            switch (compressionLevel)
            {
                case CompressionLevel.Fastest:
                    return 1;
                case CompressionLevel.NoCompression:
                    ThrowNoCompression();
                    return 0;
                case CompressionLevel.SmallestSize:
                    return MaxCompressionLevel;
                case CompressionLevel.Optimal:
                default:
                    return DefaultCompressionLevel;
            }
        }
    }
}
