using NativeCompressions.Internal;
using NativeCompressions.Interop;
using System.Buffers;
using System.Runtime.InteropServices;
using static NativeCompressions.Interop.LZ4NativeMethods;

namespace NativeCompressions;

public static partial class LZ4
{
    // Without options the length is known and gets recorded. With options, ContentSize is what the caller asked for.
    public static byte[] Compress(ReadOnlySpan<byte> source) => Compress(source, LZ4CompressionOptions.Default with { ContentSize = (ulong)source.Length });

    public static unsafe byte[] Compress(ReadOnlySpan<byte> source, in LZ4CompressionOptions options)
    {
        options.ThrowIfContentSizeDiffers(source.Length);
        var dictionary = options.Dictionary;
        var pref = options.ToPreferences();

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
                        GC.KeepAlive(dictionary); // its finalizer frees the native dictionary
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination) => Compress(source, destination, LZ4CompressionOptions.Default with { ContentSize = (ulong)source.Length });

    public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination, in LZ4CompressionOptions options)
    {
        options.ThrowIfContentSizeDiffers(source.Length);
        var dictionary = options.Dictionary;
        var pref = options.ToPreferences();

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
                    GC.KeepAlive(dictionary); // its finalizer frees the native dictionary
                }
            }
        }
    }
}
