using System.Runtime.InteropServices;

namespace NativeCompressions.Interop
{
    // Hand written bindings for functions declared under ZSTD_STATIC_LINKING_ONLY.
    // csbindgen does not emit them, but the shared library exports them.
    public static unsafe partial class ZstandardNativeMethods
    {
        /// <summary>
        ///  ZSTD_decompressBound() :
        ///  `src` should point to the start of a series of ZSTD encoded and/or skippable frames
        ///  `srcSize` must be the _exact_ size of this series (i.e. there should be a frame boundary at `src + srcSize`)
        ///  @return : upper-bound for the decompressed size of all data in all successive frames, or ZSTD_CONTENTSIZE_ERROR.
        /// </summary>
        [DllImport(__DllName, EntryPoint = "ZSTD_decompressBound", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern ulong ZSTD_decompressBound(void* src, nuint srcSize);
    }
}
