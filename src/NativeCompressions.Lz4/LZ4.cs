using NativeCompressions.LZ4.Raw;
using System.Buffers;
using System.Runtime.CompilerServices;
using static NativeCompressions.LZ4.Raw.NativeMethods;

namespace NativeCompressions.LZ4;

public static class LZ4
{
    static string? version;

    /// <summary>
    /// Gets the version string of the LZ4 library.
    /// </summary>
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

    /// <summary>
    /// Gets the version number of the LZ4 library.
    /// </summary>
    public static int VersionNumber => LZ4_versionNumber();

    /// <summary>
    /// Gets the version of the LZ4 frame format supported by the library.
    /// </summary>
    public static uint FrameVersion => LZ4F_getVersion();

    /// <summary>
    /// Get the maximum allowed compression level.
    /// </summary>
    public static int GetMaxCompressionLevel()
    {
        return LZ4F_compressionLevel_max();
    }

    public static int GetMaxCompressedLength(int inputSize, in LZ4FrameOptions options)
    {
        // TODO: same as brotli validation
        // ArgumentOutOfRangeException.ThrowIfNegative(inputSize);
        // ArgumentOutOfRangeException.ThrowIfGreaterThan(inputSize, BrotliUtils.MaxInputSize);

        ref var preferences_t = ref Unsafe.As<LZ4FrameOptions, LZ4F_preferences_t>(ref Unsafe.AsRef(in options));
        unsafe
        {
            // calculate bound for LZ4F_compressFrame
            var bound = LZ4F_compressFrameBound((nuint)inputSize, (LZ4F_preferences_t*)Unsafe.AsPointer(ref preferences_t));
            return (int)bound;
        }
    }

    public static byte[] Compress(ReadOnlySpan<byte> source) => Compress(source, LZ4FrameOptions.Default, null);

    public static unsafe byte[] Compress(ReadOnlySpan<byte> source, LZ4FrameOptions options, LZ4CompressionDictionary? dictionary)
    {
        var pref = options.ToPreferences();
        var maxLength = LZ4F_compressFrameBound((uint)source.Length, pref);

        var buffer = ArrayPool<byte>.Shared.Rent((int)maxLength);
        try
        {
            fixed (byte* src = source)
            fixed (byte* dest = buffer)
            {
                if (dictionary == null)
                {
                    var bytesWrittenOrErrorCode = LZ4F_compressFrame(dest, (nuint)buffer.Length, src, (nuint)source.Length, pref);
                    HandleErrorCode(bytesWrittenOrErrorCode);
                    return buffer.AsSpan(0, (int)bytesWrittenOrErrorCode).ToArray();
                }
                else
                {
                    LZ4F_cctx_s* cctx = default;
                    var code = LZ4F_createCompressionContext(&cctx, LZ4.FrameVersion);
                    LZ4.HandleErrorCode(code);
                    try
                    {
                        var bytesWrittenOrErrorCode = LZ4F_compressFrame_usingCDict(cctx, dest, (nuint)buffer.Length, src, (nuint)source.Length, dictionary.Handle, pref);
                        HandleErrorCode(bytesWrittenOrErrorCode);
                        return buffer.AsSpan(0, (int)bytesWrittenOrErrorCode).ToArray();
                    }
                    finally
                    {
                        LZ4F_freeCompressionContext(cctx);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="source"></param>
    /// <param name="destination">MUST be &gt;= LZ4.GetMaxCompressedLength()</param>
    /// <param name="bytesWritten"></param>
    /// <returns></returns>
    public static unsafe bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        var pref = LZ4FrameOptions.Default.ToPreferences();

        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            var bytesWrittenOrErrorCode = LZ4F_compressFrame(dest, (nuint)destination.Length, src, (nuint)source.Length, pref);
            HandleErrorCode(bytesWrittenOrErrorCode); // TODO: should check operation code and return false;
            bytesWritten = (int)bytesWrittenOrErrorCode;
            return true;
        }
    }

    // TODO: Compress: options, dictionary


    // PipeReader? ReadOnlySequence<T>? Stream?
    public static unsafe byte[] Decompress(ReadOnlySpan<byte> source)
    {
        // LZ4F_decompress
        LZ4F_dctx_s* dctx = default; // TODO: create from DecompressOptions?
        var code = LZ4F_createDecompressionContext(&dctx, FrameVersion);
        HandleErrorCode(code);
        // LZ4F_getFrameInfo



        var destination = new byte[60000]; // TODO: which bytes?

        var totalWritten = 0;
        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {

            // LZ4F_getFrameInfo()
            var consumedSourceLength = (nuint)source.Length;
            var writtenDestinationLength = (nuint)destination.Length;


            //LZ4F_frameInfo_t finfo = default;
            //var code2 = LZ4F_getFrameInfo(dctx, &finfo, src, &consumedSourceLength);

            // an hint of how many `srcSize` bytes LZ4F_decompress() expects for next call.

            var hintOrErrorCode = LZ4F_decompress(dctx, dest, &writtenDestinationLength, src, &consumedSourceLength, null);
            totalWritten += (int)writtenDestinationLength;




            if (hintOrErrorCode == 0)
            {
                // success decompression.
                return destination.AsSpan(0, totalWritten).ToArray();
            }

            if (LZ4F_isError(hintOrErrorCode) != 0)
            {
                LZ4.HandleErrorCode(hintOrErrorCode);
                // error...
            }
        }

        return destination;
    }


    internal static void HandleErrorCode(nuint code)
    {
        if (LZ4F_isError(code) != 0)
        {
            var error = GetErrorName(code);
            throw new LZ4Exception(error);
        }
    }

    static unsafe string GetErrorName(nuint code)
    {
        var name = (sbyte*)LZ4F_getErrorName(code);
        return new string(name);
    }
}
