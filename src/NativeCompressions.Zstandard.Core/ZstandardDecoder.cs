using NativeCompressions.Internal;
using NativeCompressions.Zstandard.Raw;
using System.Buffers;
using static NativeCompressions.Zstandard.Raw.NativeMethods;

namespace NativeCompressions.Zstandard;

/// <summary>
/// Provides streaming decompression functionality for Zstandard format.
/// </summary>
public unsafe struct ZstandardDecoder : IDisposable
{
    ZSTD_DCtx_s* context;
    bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ZstandardDecoder"/> struct.
    /// </summary>
    public ZstandardDecoder()
        : this(ZstandardDecompressionOptions.Default, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZstandardDecoder"/> struct with specified options.
    /// </summary>
    public ZstandardDecoder(in ZstandardDecompressionOptions decompressionOptions, ZstandardCompressionDictionary? dictionary = null)
    {
        // we hold handle in raw, does not wrap SafeHandle so be careful to use it.
        context = ZSTD_createDCtx();
        if (context == null) throw new ZstandardException("Failed to create decompression context");

        decompressionOptions.SetParameter(context);
        dictionary?.SetDictionary(context);
    }

    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
    {
        return Decompress(source, destination, out bytesConsumed, out bytesWritten, out _);
    }

    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, out int hintOfNextSrcSize)
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

            // @return : 0 when a frame is completely decoded and fully flushed,
            //   or an error code, which can be tested using ZSTD_isError(),
            //   or any other value > 0, which means there is still some decoding or flushing to do to complete current frame:
            //     the return value is a suggested next input size(just a hint for better latency)
            //     that will never request more than the remaining frame size.
            var hintOrErrorCode = ZSTD_decompressStream(context, &output, &input);

            bytesConsumed = (int)input.pos;
            bytesWritten = (int)output.pos;
            hintOfNextSrcSize = (int)hintOrErrorCode;

            if (Zstandard.IsError(hintOrErrorCode))
            {
                return OperationStatus.InvalidData;
            }

            if (hintOrErrorCode == 0)
            {
                return OperationStatus.Done;
            }

            var sourceFullyConsumed = input.pos == input.size;
            var destinationFullyUsed = output.pos == output.size;

            var result = (sourceFullyConsumed, destinationFullyUsed) switch
            {
                // both full, remains output data exists in native context
                (true, true) => OperationStatus.DestinationTooSmall,

                // source is fully consumed but output buffer has space, need more input data
                (true, false) => OperationStatus.NeedMoreData,

                // output buffer is full but input remains, need larger output buffer
                (false, true) => OperationStatus.DestinationTooSmall,

                // others
                (false, false) => (bytesConsumed > 0 || bytesWritten > 0) // any progress?
                    ? OperationStatus.NeedMoreData
                    : OperationStatus.InvalidData
            };

            return result;
        }
    }

    public void Reset()
    {
        ValidateDisposed();

        var result = ZSTD_DCtx_reset(context, (int)ZSTD_ResetDirective.ZSTD_reset_session_only);
        Zstandard.ThrowIfError(result);
    }

    public void Reset(in ZstandardDecompressionOptions options, ZstandardCompressionDictionary? dictionary = null)
    {
        ValidateDisposed();

        var result = ZSTD_DCtx_reset(context, (int)ZSTD_ResetDirective.ZSTD_reset_session_and_parameters);
        Zstandard.ThrowIfError(result);

        options.SetParameter(context);
        dictionary?.SetDictionary(context);
    }

    void ValidateDisposed()
    {
        if (disposed) Throws.ObjectDisposedException();
        if (context == null) Throws.InvalidContextNullException();
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

    enum ZSTD_dParameter
    {
        ZSTD_d_windowLogMax = 100,
        ZSTD_d_format = 101,
        ZSTD_d_stableOutBuffer = 102,
        ZSTD_d_forceIgnoreChecksum = 103,
        ZSTD_d_refMultipleDDicts = 104
    }

    enum ZSTD_ResetDirective
    {
        ZSTD_reset_session_only = 1,
        ZSTD_reset_parameters = 2,
        ZSTD_reset_session_and_parameters = 3
    }
}
