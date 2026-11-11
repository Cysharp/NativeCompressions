using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeCompressions.Interop;
using static NativeCompressions.Interop.ZstandardNativeMethods;

namespace NativeCompressions;

// ZSTD_dParameter + dictionary ref

[StructLayout(LayoutKind.Auto)]
public readonly record struct ZstandardDecompressionOptions
{
    public static readonly ZstandardDecompressionOptions Default = new ZstandardDecompressionOptions();

    readonly int windowLogMax;
    readonly ZstandardDictionary? dictionary;

    /// <summary>
    /// Select a size limit (in power of 2) beyond which
    /// the streaming API will refuse to allocate memory buffer
    /// in order to protect the host from unreasonable memory requirements.
    /// This parameter is only useful in streaming mode, since no internal buffer is allocated in single-pass mode.
    /// By default, a decompression context accepts window sizes &lt;= (1 &lt;&lt; ZSTD_WINDOWLOG_LIMIT_DEFAULT).
    /// Special: value 0 means "use default maximum windowLog".
    /// </summary>
    /// <remarks>
    /// The limit applies where zstd decodes in streaming mode: <see cref="ZstandardDecoder"/>, <see cref="ZstandardStream"/>,
    /// <c>Zstandard.DecompressAsync</c>, and <c>Zstandard.Decompress</c> returning an array without trustedData.
    /// <c>Zstandard.Decompress</c> into a span, <c>Zstandard.TryDecompress</c>, and trustedData for input whose size the frame headers give,
    /// decode in a single pass into a buffer whose size is already decided. That allocates no window, so the limit is not checked there.
    /// </remarks>
    public int WindowLogMax
    {
        get => windowLogMax;
        init => windowLogMax = value;
    }

    public ZstandardDictionary? Dictionary
    {
        get => dictionary;
        init => dictionary = value;
    }

    internal ZstandardDictionary.Lease AcquireDictionary() => dictionary == null ? default : dictionary.Acquire();

    // The lease is the one taken on the dictionary of these options.
    internal unsafe void SetParameter(ZSTD_DCtx_s* context, in ZstandardDictionary.Lease lease)
    {
        SetParameter(context, ZSTD_dParameter.ZSTD_d_windowLogMax, windowLogMax);
        if (!lease.IsEmpty)
        {
            var code = ZSTD_DCtx_refDDict(context, lease.Decompression);
            Zstandard.ThrowIfError(code);
        }
    }

    // Set parameter if value is not zero(default).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void SetParameter(ZSTD_DCtx_s* context, ZSTD_dParameter parameter, int value)
    {
        if (value != 0)
        {
            var code = ZSTD_DCtx_setParameter(context, parameter, value);
            Zstandard.ThrowIfError(code);
        }
    }
}
