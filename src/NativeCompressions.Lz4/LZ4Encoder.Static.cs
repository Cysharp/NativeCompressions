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

        static bool initFrameVersion;
        static uint frameVersion;

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

        public static uint FrameVersion
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (!initFrameVersion)
                {
                    SetFrameversion();
                }

                return frameVersion;

                static void SetFrameversion()
                {
                    frameVersion = LZ4F_getVersion();
                    initFrameVersion = true;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetMaxBlockCompressedLength(int inputSize)
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
            var destSize = GetMaxBlockCompressedLength(source.Length);

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
            var destSize = GetMaxBlockCompressedLength(source.Length);

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
            var destSize = GetMaxBlockCompressedLength(source.Length);

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

        // Frame Format

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetMaxFrameCompressedLength(int inputSize, bool withHeader = true)
        {
            // TODO: LZ4F_preferences_t
            // TODO: require footer?
            unsafe
            {
                if (withHeader)
                {
                    return (int)LZ4F_compressBound((uint)inputSize, null) + MaxFrameHeaderLength;
                }
                else
                {
                    return (int)LZ4F_compressBound((uint)inputSize, null);
                }
            }
        }

        // #define LZ4F_HEADER_SIZE_MIN  7   /* LZ4 Frame header size can vary, depending on selected parameters */
        // #define LZ4F_HEADER_SIZE_MAX 19
        public const int MaxFrameHeaderLength = 19;

        public static byte[] CompressFrame(ReadOnlySpan<byte> source)
        {
            var destSize = GetMaxFrameCompressedLength(source.Length);

            if (destSize < 512)
            {
                Span<byte> dest = stackalloc byte[destSize];
                if (!TryCompressFrame(source, dest, out var bytesWritten))
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
                    if (!TryCompressFrame(source, dest, out var bytesWritten))
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

        public static bool TryCompressFrame(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
        {
            unsafe
            {
                fixed (void* src = &MemoryMarshal.GetReference(source))
                fixed (void* dest = &MemoryMarshal.GetReference(destination))
                {
                    //var prefs = new LZ4F_preferences_t
                    //{
                    //    frameInfo = new LZ4F_frameInfo_t
                    //    {
                    //        contentSize = (ulong)source.Length
                    //    },
                    //};

                    var sizeOrCode = LZ4F_compressFrame(dest, (nuint)destination.Length, src, (nuint)source.Length, null);

                    if (LZ4F_isError(sizeOrCode) != 0)
                    {
                        var error = GetErrorName(sizeOrCode);
                        throw new Exception(error); // TODO: throw;
                    }

                    bytesWritten = (int)sizeOrCode;
                    return true;
                }
            }
        }


        [DoesNotReturn]
        static void Throw()
        {
            // TODO:
            throw new Exception();
        }

        static unsafe string GetErrorName(nuint code)
        {
            var name = (sbyte*)LZ4F_getErrorName(code);
            return new string(name);
        }
    }
}
