using System.Buffers;
using size_t = System.UIntPtr; // nuint is .NET size_t equivalent, internally nuint is represent as UIntPtr.

namespace NativeCompressions.ZStandard;

/// <summary>
///   When compressing many times,
///   it is recommended to allocate a context just once,
///   and re-use it for each successive compression operation.
///   This will make workload friendlier for system's memory.
///   Note: re-using context is just a speed / resource optimization.
///         It doesn't change the compression ratio, which remains identical.
///   Note 2 : In multi-threaded environments,
///            use one different context per thread for parallel execution.
/// </summary>
public sealed unsafe class CompressionContext : IDisposable
{
    readonly void* compressionContext;

    bool disposedValue;

    public CompressionContext()
    {
        compressionContext = LibZstd.ZSTD_createCCtx();
    }

    // TODO:more api
    public byte[] Compress(byte[] src, int? compressionLevel = default)
    {
        var level = compressionLevel ?? ZStandard.DefaultCompressionLevel;
        var dest = ArrayPool<byte>.Shared.Rent((int)LibZstd.ZSTD_compressBound((size_t)src.Length));
        try
        {
            fixed (byte* srcP = src)
            fixed (byte* destP = dest)
            {
                var compressedSize = LibZstd.ZSTD_compressCCtx(compressionContext, destP, (size_t)dest.Length, srcP, (size_t)src.Length, level);

                if ((uint)compressedSize > dest.Length)
                {
                    throw new InvalidOperationException("Failed ZSTD_compress, dstCapacity is invalid.");
                }

                var isError = LibZstd.ZSTD_isError(compressedSize);
                if (isError != 0)
                {
                    throw new InvalidOperationException("Failed ZSTD_compress, " + ZStandard.GetErrorName(compressedSize));
                }

                return dest.AsSpan(0, (int)compressedSize).ToArray();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(dest);
        }
    }

    private void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            disposedValue = true;

            LibZstd.ZSTD_freeCCtx(compressionContext);
        }
    }

    ~CompressionContext()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}