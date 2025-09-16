using NativeCompressions.Zstandard.Raw;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static NativeCompressions.Zstandard.Raw.NativeMethods;

namespace NativeCompressions.Zstandard;

/// <summary>
/// Represents as ZSTD_dParameter
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ZstandardDecompressionOptions
{
    public static readonly ZstandardDecompressionOptions Default = new ZstandardDecompressionOptions();

    public bool IsDefault
    {
        get
        {
            if (windowLogMax == 0)
            {
                return true;
            }
            return false;
        }
    }

    readonly int windowLogMax;

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

    internal unsafe void SetParameter(ZSTD_DCtx_s* context)
    {
        if (IsDefault) return;

        SetParameter(context, ZSTD_dParameter.ZSTD_d_windowLogMax, windowLogMax);
    }

    // Set parameter if value is not zero(default).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void SetParameter(ZSTD_DCtx_s* context, ZSTD_dParameter parameter, int value)
    {
        if (value != 0)
        {
            var code = ZSTD_DCtx_setParameter(context, (int)parameter, value);
            if (Zstandard.IsError(code)) // for inlining
            {
                Zstandard.ThrowAsError(code);
            }
        }
    }

    enum ZSTD_dParameter
    {
        ZSTD_d_windowLogMax = 100,
        // others are experimental so ignore.
    }
}
