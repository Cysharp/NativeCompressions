using size_t = System.UIntPtr; // nuint is .NET size_t equivalent, internally nuint is represent as UIntPtr.

namespace NativeCompressions.ZStandard;

/// <summary>
/// Managed ZSTD_CStream operations.d
/// </summary>
public sealed unsafe class CompressionStreamContext : IDisposable
{
    readonly void* compressionStream;
    bool disposedValue;

    public CompressionStreamContext()
    {
        compressionStream = LibZstd.ZSTD_createCStream();
    }

    public int Write(ReadOnlySpan<byte> src, Span<byte> dest)
    {
        fixed (byte* srcPtr = src)
        fixed (byte* destPtr = dest)
        {
            var inBuffer = new LibZstd.ZSTD_inBuffer()
            {
                src = srcPtr,
                size = (size_t)src.Length,
                pos = (size_t)0,
            };

            var outBuffer = new LibZstd.ZSTD_outBuffer()
            {
                src = destPtr,
                size = (size_t)dest.Length,
                pos = (size_t)0,
            };

            var result = LibZstd.ZSTD_compressStream2(compressionStream, ref outBuffer, ref inBuffer, LibZstd.ZSTD_EndDirective.ZSTD_e_continue);

            // TODO:check result
            return (int)result;
        }
    }

    void Flush()
    {




    }

    void Complete()
    {
    }

    private void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            disposedValue = true;
            LibZstd.ZSTD_freeCStream(compressionStream);
        }
    }

    ~CompressionStreamContext()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}