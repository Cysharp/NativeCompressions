using NativeCompressions.Interop;
using static NativeCompressions.Interop.OpenZLNativeMethods;

namespace CatalystSmoke;

// OpenZL's public high-level API is not enabled yet. Exercise its existing binding.
internal static unsafe class OpenZLSmoke
{
    internal static uint Run()
    {
        var version = ZL_getDefaultEncodingVersion();
        if (version == 0) throw new InvalidOperationException("Invalid OpenZL encoding version.");
        var source = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray();
        foreach (var graph in new[] { ZL_StandardGraphID.ZL_StandardGraphID_zstd, ZL_StandardGraphID.ZL_StandardGraphID_lz4 })
        {
            var cctx = ZL_CCtx_create();
            var compressor = ZL_Compressor_create();
            var dctx = ZL_DCtx_create();
            try
            {
                if (cctx == null || compressor == null || dctx == null) throw new OutOfMemoryException();
                Check(ZL_Compressor_setParameter(compressor, ZL_CParam.ZL_CParam_formatVersion, checked((int)version)));
                Check(ZL_Compressor_selectStartingGraphID(compressor, new ZL_GraphID { gid = (uint)graph }));
                Check(ZL_CCtx_refCompressor(cctx, compressor));
                // Ample capacity for this fixed input; errors are checked before reading sizes.
                var compressed = new byte[source.Length * 2 + 65536];
                var restored = new byte[source.Length];
                fixed (byte* src = source, dst = compressed, result = restored)
                {
                    var size = Check(ZL_CCtx_compress(cctx, dst, (nuint)compressed.Length, src, (nuint)source.Length));
                    if (size > (nuint)compressed.Length) throw new InvalidOperationException("Invalid OpenZL compressed size.");
                    var length = Check(ZL_DCtx_decompress(dctx, result, (nuint)restored.Length, dst, size));
                    if (length != (nuint)source.Length || !source.AsSpan().SequenceEqual(restored))
                        throw new InvalidOperationException($"OpenZL {graph} round trip mismatch.");
                }
            }
            finally
            {
                if (cctx != null) ZL_CCtx_free(cctx);
                if (compressor != null) ZL_Compressor_free(compressor);
                if (dctx != null) ZL_DCtx_free(dctx);
            }
        }
        return version;
    }

    static nuint Check(ZL_Result_size_t_u result)
    {
        if (ZL_isErrorBool(result))
            throw new InvalidOperationException($"OpenZL failed: {new string((sbyte*)ZL_ErrorCode_toString(result._code))}");
        return result._value._value;
    }
}
