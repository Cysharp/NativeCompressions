using NativeCompressions.ZStandard.Internal;
using NativeCompressions.ZStandard.Raw;
using System.Buffers;
using static NativeCompressions.ZStandard.Raw.NativeMethods;

namespace NativeCompressions.ZStandard;

/// <summary>
/// Provides streaming decompression functionality for ZStandard format.
/// </summary>
public unsafe struct ZStandardDecoder : IDisposable
{
    ZSTD_DCtx_s* context;
    ZStandardCompressionDictionary? dictionary;
    bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ZStandardDecoder"/> struct.
    /// </summary>
    public ZStandardDecoder()
        : this(null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZStandardDecoder"/> struct with a dictionary.
    /// </summary>
    public ZStandardDecoder(ZStandardCompressionDictionary? dictionary)
    {
        context = ZSTD_createDCtx();
        if (context == null)
            throw new ZStandardException("Failed to create decompression context");

        this.dictionary = dictionary;
        this.disposed = false;

        // Load dictionary if provided
        if (dictionary != null)
        {
            var result = ZSTD_DCtx_refDDict(context, dictionary.DecompressionHandle);
            ZStandard.ThrowIfError(result);
        }
    }

    /// <summary>
    /// Decompresses source data and writes the result to the destination buffer.
    /// </summary>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
    {
        ValidateDisposed();

        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            var input = new ZSTD_inBuffer_s
            {
                src = src,
                size = (nuint)source.Length,
                pos = 0
            };

            var output = new ZSTD_outBuffer_s
            {
                dst = dest,
                size = (nuint)destination.Length,
                pos = 0
            };

            var result = ZSTD_decompressStream(context, &output, &input);
            
            bytesConsumed = (int)input.pos;
            bytesWritten = (int)output.pos;

            if (ZStandard.IsError(result))
            {
                throw new ZStandardException(ZStandard.GetErrorName(result));
            }

            // result == 0 means frame is completely decoded
            if (result == 0)
            {
                return OperationStatus.Done;
            }

            // If we consumed all input but still have more to decode
            if (input.pos == input.size)
            {
                return OperationStatus.NeedMoreData;
            }

            // If we filled the output buffer
            if (output.pos == output.size)
            {
                return OperationStatus.DestinationTooSmall;
            }

            // Continue processing
            return OperationStatus.NeedMoreData;
        }
    }

    /// <summary>
    /// Resets the decoder to start a new decompression session.
    /// </summary>
    public void Reset()
    {
        ValidateDisposed();
        
        var result = ZSTD_DCtx_reset(context, (int)ZSTD_ResetDirective.ZSTD_reset_session_only);
        ZStandard.ThrowIfError(result);
    }

    /// <summary>
    /// Sets the maximum window size for decompression.
    /// </summary>
    public void SetMaxWindowSize(int windowSizeMax)
    {
        ValidateDisposed();
        
        var result = ZSTD_DCtx_setParameter(context, (int)ZSTD_dParameter.ZSTD_d_windowLogMax, windowSizeMax);
        ZStandard.ThrowIfError(result);
    }

    void ValidateDisposed()
    {
        if (disposed) Throws.ObjectDisposedException();
    }

    public void Dispose()
    {
        if (!disposed && context != null)
        {
            ZSTD_freeDCtx(context);
            context = null;
            disposed = true;
        }
    }
}

// Enums for ZStandard decompression parameters
internal enum ZSTD_dParameter
{
    ZSTD_d_windowLogMax = 100,
    ZSTD_d_format = 101,
    ZSTD_d_stableOutBuffer = 102,
    ZSTD_d_forceIgnoreChecksum = 103,
    ZSTD_d_refMultipleDDicts = 104
}
