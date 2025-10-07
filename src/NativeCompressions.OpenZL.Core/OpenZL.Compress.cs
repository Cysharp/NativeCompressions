using static NativeCompressions.Interop.OpenZLNativeMethods;

namespace NativeCompressions;

public static partial class OpenZL
{
    public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            var context = ZL_CCtx_create();
            try
            {
                // ZL_setfor

                // written or error
                // TODO: is error code? https://github.com/facebook/openzl/blob/d177e9f32bd5ac037d54a2af5f7588b841aeb47d/include/openzl/zl_errors_types.h
                var written = ZL_CCtx_compress(context, dest, (nuint)destination.Length, src, (nuint)source.Length);
                return written._code;
            }
            finally
            {
                ZL_CCtx_free(context);
            }
        }
    }
}
