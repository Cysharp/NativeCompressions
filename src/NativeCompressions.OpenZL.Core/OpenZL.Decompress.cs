using static NativeCompressions.Interop.OpenZLNativeMethods;

namespace NativeCompressions;

public static partial class OpenZL
{
    public static unsafe int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            var dctx = ZL_DCtx_create();
            try
            {
                var written = ZL_DCtx_decompress(dctx, dest, (nuint)destination.Length, src, (nuint)source.Length);
                // TODO: check error code
                return (int)written._value._value;
            }
            finally
            {
                ZL_DCtx_free(dctx);
            }
        }
    }
}
