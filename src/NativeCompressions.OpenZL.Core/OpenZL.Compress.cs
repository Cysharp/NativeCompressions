using NativeCompressions.Interop;
using static NativeCompressions.Interop.OpenZLNativeMethods;

namespace NativeCompressions;

public static partial class OpenZL
{
    public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            // simple sample: https://github.com/facebook/openzl/blob/dev/examples/compress_app.cpp

            var cctx = ZL_CCtx_create();
            try
            {
                var cgraph = ZL_Compressor_create();
                try
                {
                    const int ZSTRONG_EXAMPLE_FORMAT_VERSION = 16;

                    ThrowIfError(ZL_Compressor_setParameter(cgraph, ZL_CParam.ZL_CParam_formatVersion, ZSTRONG_EXAMPLE_FORMAT_VERSION));
                    ThrowIfError(ZL_Compressor_selectStartingGraphID(cgraph, new ZL_GraphID { gid = (uint)ZL_StandardGraphID.ZL_StandardGraphID_zstd }));
                    ThrowIfError(ZL_CCtx_refCompressor(cctx, cgraph));

                    // written or error
                    var written = ZL_CCtx_compress(cctx, dest, (nuint)destination.Length, src, (nuint)source.Length);
                    ThrowIfError(written);

                    return (int)written._value._value;
                }
                finally
                {
                    ZL_Compressor_free(cgraph);
                }
            }
            finally
            {
                ZL_CCtx_free(cctx);
            }
        }
    }
}
