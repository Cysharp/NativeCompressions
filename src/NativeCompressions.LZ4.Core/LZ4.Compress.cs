using NativeCompressions.Internal;
using NativeCompressions.Interop;
using System.Buffers;
using System.Runtime.InteropServices;
using static NativeCompressions.Interop.LZ4NativeMethods;

namespace NativeCompressions;

public static partial class LZ4
{
    public static byte[] Compress(ReadOnlySpan<byte> source) => Compress(source, LZ4CompressionOptions.Default);

    public static unsafe byte[] Compress(ReadOnlySpan<byte> source, in LZ4CompressionOptions options)
    {
        var dictionary = options.Dictionary;
        var pref = options.ToPreferencesWithContentSize(options.ContentSize);

        var maxLength = LZ4F_compressFrameBound((uint)source.Length, &pref);
        var buffer = ArrayPool<byte>.Shared.Rent((int)maxLength);
        try
        {
            fixed (byte* src = source)
            fixed (byte* dest = buffer)
            {
                if (dictionary == null)
                {
                    var bytesWrittenOrErrorCode = LZ4F_compressFrame(dest, (nuint)buffer.Length, src, (nuint)source.Length, &pref);
                    ThrowIfError(bytesWrittenOrErrorCode);
                    return buffer.AsSpan(0, (int)bytesWrittenOrErrorCode).ToArray();
                }
                else
                {
                    LZ4F_cctx_s* cctx = default;
                    var code = LZ4F_createCompressionContext(&cctx, LZ4.FrameVersion);
                    LZ4.ThrowIfError(code);
                    try
                    {
                        var bytesWrittenOrErrorCode = LZ4F_compressFrame_usingCDict(cctx, dest, (nuint)buffer.Length, src, (nuint)source.Length, dictionary.Handle, &pref);
                        ThrowIfError(bytesWrittenOrErrorCode);
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

    public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination) => Compress(source, destination, LZ4CompressionOptions.Default);

    public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination, in LZ4CompressionOptions options)
    {
        var dictionary = options.Dictionary;
        var pref = options.ToPreferencesWithContentSize((ulong)source.Length);

        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            if (dictionary == null)
            {
                var bytesWrittenOrErrorCode = LZ4F_compressFrame(dest, (nuint)destination.Length, src, (nuint)source.Length, &pref);
                ThrowIfError(bytesWrittenOrErrorCode);
                return (int)bytesWrittenOrErrorCode;
            }
            else
            {
                LZ4F_cctx_s* cctx = default;
                var code = LZ4F_createCompressionContext(&cctx, LZ4.FrameVersion);
                LZ4.ThrowIfError(code);
                try
                {
                    var bytesWrittenOrErrorCode = LZ4F_compressFrame_usingCDict(cctx, dest, (nuint)destination.Length, src, (nuint)source.Length, dictionary.Handle, &pref);
                    ThrowIfError(bytesWrittenOrErrorCode);
                    return (int)bytesWrittenOrErrorCode;
                }
                finally
                {
                    LZ4F_freeCompressionContext(cctx);
                }
            }
        }
    }
}
