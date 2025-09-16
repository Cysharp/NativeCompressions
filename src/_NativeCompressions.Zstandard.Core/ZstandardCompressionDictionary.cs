using NativeCompressions.Zstandard.Raw;
using System.Runtime.InteropServices;
using static NativeCompressions.Zstandard.Raw.NativeMethods;

namespace NativeCompressions.Zstandard;

public sealed class ZstandardCompressionDictionary : SafeHandle
{
    public override bool IsInvalid => handle == IntPtr.Zero;

    // manage two handles(Compression and Decompression)
    public unsafe ZSTD_CDict_s* CompressionHandle => ((ZSTD_CDict_s*)handle);
    public unsafe ZSTD_DDict_s* DecompressionHandle { get; private set; }

    public ZstandardCompressionDictionary(ReadOnlySpan<byte> dictionaryData, int compressionLevel = Zstandard.DefaultCompressionLevel)
        : base(IntPtr.Zero, true)
    {
        unsafe
        {
            fixed (void* p = dictionaryData)
            {
                // Create compression dictionary
                var cHandle = ZSTD_createCDict(p, (nuint)dictionaryData.Length, compressionLevel);
                if (cHandle == null)
                {
                    throw new ZstandardException("Failed to create compression dictionary");
                }
                SetHandle((IntPtr)cHandle);

                // Create decompression dictionary
                DecompressionHandle = ZSTD_createDDict(p, (nuint)dictionaryData.Length);
                if (DecompressionHandle == null)
                {
                    ZSTD_freeCDict(cHandle);
                    throw new ZstandardException("Failed to create decompression dictionary");
                }
            }
        }
    }

    protected override unsafe bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero)
        {
            unsafe
            {
                ZSTD_freeCDict((ZSTD_CDict_s*)handle);
                if (DecompressionHandle != null)
                {
                    ZSTD_freeDDict(DecompressionHandle);
                    DecompressionHandle = null;
                }
            }
            handle = IntPtr.Zero;
            DecompressionHandle = null;
            return true;
        }
        return false;
    }

    internal unsafe void SetDictionary(ZSTD_CCtx_s* context)
    {
        var result = ZSTD_CCtx_refCDict(context, CompressionHandle);
        Zstandard.ThrowIfError(result);
    }

    internal unsafe void SetDictionary(ZSTD_DCtx_s* context)
    {
        var result = ZSTD_DCtx_refDDict(context, DecompressionHandle);
        Zstandard.ThrowIfError(result);
    }
}
