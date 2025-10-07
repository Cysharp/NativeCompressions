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

    internal unsafe void SetParameter(ZSTD_DCtx_s* context)
    {
        SetParameter(context, ZSTD_dParameter.ZSTD_d_windowLogMax, windowLogMax);
        if (dictionary != null)
        {
            var code = ZSTD_DCtx_refDDict(context, dictionary.DecompressionHandle);
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
