using NativeCompressions.Internal;
using NativeCompressions.ZStandard.Raw;
using System.Buffers;
using static NativeCompressions.ZStandard.Raw.NativeMethods;

namespace NativeCompressions.ZStandard;

/// <summary>
/// Provides streaming compression functionality for ZStandard format.
/// </summary>
public unsafe struct ZStandardEncoder : IDisposable
{
    ZSTD_CCtx_s* context;
    bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ZStandardEncoder"/> struct with default settings.
    /// </summary>
    public ZStandardEncoder()
        : this(ZStandardCompressionOptions.Default, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZStandardEncoder"/> struct with compressionLevel.
    /// </summary>
    public ZStandardEncoder(int compressionLevel)
        : this(new ZStandardCompressionOptions(compressionLevel), null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZStandardEncoder"/> struct with specified options.
    /// </summary>
    public ZStandardEncoder(in ZStandardCompressionOptions compressionOptions, ZStandardCompressionDictionary? dictionary = null)
    {
        // we hold handle in raw, does not wrap SafeHandle so be careful to use it.
        this.context = ZSTD_createCCtx();
        if (context == null) throw new ZStandardException("Failed to create compression context");

        compressionOptions.SetParameter(context);
        dictionary?.SetDictionary(context);
    }

    /// <summary>
    /// Compresses source data and writes the result to the destination buffer.
    /// </summary>
    /// <param name="source">The data to compress. May be empty.</param>
    /// <param name="destination">The buffer to write compressed data to.</param>
    /// <param name="bytesConsumed">When this method returns, contains the number of bytes read from source.</param>
    /// <param name="bytesWritten">When this method returns, contains the number of bytes written to destination.</param>
    /// <param name="isFinalBlock">true to finalize the internal stream; false to continue streaming.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/>: All input was consumed and compressed data was written to destination. If isFinalBlock is false, the encoder is ready for more input.
    /// <see cref="OperationStatus.DestinationTooSmall"/>: The destination buffer is too small to hold the compressed data. Provide a larger buffer and call again.
    /// <see cref="OperationStatus.InvalidData"/>: The compression operation failed due to invalid state or parameters.
    /// </returns>
    /// <remarks>
    /// This method follows the same pattern as System.IO.Compression.BrotliEncoder.
    /// When isFinalBlock is true, the method will attempt to flush all internal buffers and finalize the frame.
    /// The method is designed to be called multiple times for streaming scenarios.
    /// </remarks>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)
    {
        ValidateDisposed();
        var endOp = isFinalBlock ? ZSTD_EndDirective.ZSTD_e_end : ZSTD_EndDirective.ZSTD_e_continue;
        return CompressCore(source, destination, out bytesConsumed, out bytesWritten, endOp);
    }

    public OperationStatus Flush(Span<byte> destination, out int bytesWritten)
    {
        ValidateDisposed();
        return CompressCore([], destination, out _, out bytesWritten, ZSTD_EndDirective.ZSTD_e_flush);
    }

    public OperationStatus Close(Span<byte> destination, out int bytesWritten)
    {
        ValidateDisposed();
        return CompressCore([], destination, out _, out bytesWritten, ZSTD_EndDirective.ZSTD_e_end);
    }

    OperationStatus CompressCore(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, ZSTD_EndDirective endOperation)
    {
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

            // @return provides a minimum amount of data remaining to be flushed from internal buffers or an error code
            var remaining = ZSTD_compressStream2(context, &output, &input, (int)endOperation);
            if (ZStandard.IsError(remaining))
            {
                bytesWritten = 0;
                bytesConsumed = 0;
                return OperationStatus.InvalidData;
            }

            bytesConsumed = (int)input.pos;
            bytesWritten = (int)output.pos;

            // source is fully consumed and fully flushed in ZStdContext internal buffer.
            if ((int)input.pos == source.Length && remaining == 0)
            {
                return OperationStatus.Done;
            }

            // source is fully consumed
            if (input.pos == input.size)
            {
                // If operation is final-block and remains data in internal buffer
                if (endOperation == ZSTD_EndDirective.ZSTD_e_end && remaining > 0)
                {
                    return OperationStatus.DestinationTooSmall;
                }

                return OperationStatus.Done;
            }

            // source is not consumed fully
            return OperationStatus.DestinationTooSmall;
        }
    }

    /// <summary>
    /// Resets the encoder to start a new compression session.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the encoder has been disposed.</exception>
    /// <exception cref="ZStandardException">Thrown when the reset operation fails.</exception>
    /// <remarks>
    /// This method resets only the session state, preserving all compression parameters.
    /// After calling Reset(), the encoder is ready to compress a new frame with the same settings.
    /// Any buffered data from the previous compression session is discarded.
    /// </remarks>
    public void Reset()
    {
        ValidateDisposed();

        var result = ZSTD_CCtx_reset(context, (int)ZSTD_ResetDirective.ZSTD_reset_session_only);
        ZStandard.ThrowIfError(result);
    }

    public void Reset(in ZStandardCompressionOptions options, ZStandardCompressionDictionary? dictionary = null)
    {
        ValidateDisposed();

        var result = ZSTD_CCtx_reset(context, (int)ZSTD_ResetDirective.ZSTD_reset_session_and_parameters);
        ZStandard.ThrowIfError(result);

        options.SetParameter(context);
        dictionary?.SetDictionary(context);
    }

    void ValidateDisposed()
    {
        if (disposed) Throws.ObjectDisposedException();
    }

    public void Dispose()
    {
        if (!disposed && context != null)
        {
            ZSTD_freeCCtx(context);
            context = null;
            disposed = true;
        }
    }

    enum ZSTD_EndDirective
    {
        ZSTD_e_continue = 0,
        ZSTD_e_flush = 1,
        ZSTD_e_end = 2
    }

    enum ZSTD_ResetDirective
    {
        ZSTD_reset_session_only = 1,
        ZSTD_reset_parameters = 2,
        ZSTD_reset_session_and_parameters = 3
    }
}
