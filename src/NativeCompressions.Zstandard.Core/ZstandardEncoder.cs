using NativeCompressions.Internal;
using NativeCompressions.Interop;
using System.Buffers;
using System.Runtime.InteropServices;
using static NativeCompressions.Interop.ZstandardNativeMethods;

namespace NativeCompressions;

// BrotliEncoder/Decoder is a combination of a struct and a native context wrapped in SafeHandle.
// In this case, the outer layer is a struct, but there is a SafeHandle allocation.
// For NativeCompressions' Encoder/Decoder, we made it a class and turned the outer layer itself into a SafeHandle.
// Since LZ4/Zstandard's native contexts are reusable, we have given the Encoder/Decoder a reusable nature as well.
// In that case, if we make it a struct and allocate a raw native context for zero allocation, the risk of leaks increases.
// Therefore, we compared safety and allocation cost, and adopted SafeHandle to ensure safety.

/// <summary>
/// Provides streaming compression functionality for Zstandard format.
/// </summary>
public unsafe class ZstandardEncoder : SafeHandle
{
    // ZSTD_CCtx_s* base.handle

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>
    /// Initializes a new instance of the <see cref="ZstandardEncoder"/> with default settings.
    /// </summary>
    public ZstandardEncoder()
        : this(Zstandard.DefaultCompressionLevel)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZstandardEncoder"/> with compressionLevel.
    /// </summary>
    public ZstandardEncoder(int compressionLevel)
        : base(IntPtr.Zero, true)
    {
        var context = ZSTD_createCCtx();
        if (context == null) throw new ZstandardException("Failed to create compression context");

        // setup without create ZstandardCompressionOptions
        if (compressionLevel != Zstandard.DefaultCompressionLevel)
        {
            var result = ZSTD_CCtx_setParameter(context, ZSTD_cParameter.ZSTD_c_compressionLevel, compressionLevel);
            Zstandard.ThrowIfError(result);
        }

        SetHandle((IntPtr)context); // assign to SafeHandle
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZstandardEncoder"/> with specified options.
    /// </summary>
    public ZstandardEncoder(in ZstandardCompressionOptions compressionOptions)
        : base(IntPtr.Zero, true)
    {
        var context = ZSTD_createCCtx();
        if (context == null) throw new ZstandardException("Failed to create compression context");

        compressionOptions.SetParameter(context);
        SetHandle((IntPtr)context); // assign to SafeHandle
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
        Validate();
        var endOp = isFinalBlock ? ZSTD_EndDirective.ZSTD_e_end : ZSTD_EndDirective.ZSTD_e_continue;
        return CompressCore(source, destination, out bytesConsumed, out bytesWritten, endOp);
    }

    public OperationStatus Flush(Span<byte> destination, out int bytesWritten)
    {
        Validate();
        return CompressCore([], destination, out _, out bytesWritten, ZSTD_EndDirective.ZSTD_e_flush);
    }

    public OperationStatus Close(Span<byte> destination, out int bytesWritten)
    {
        Validate();
        return CompressCore([], destination, out _, out bytesWritten, ZSTD_EndDirective.ZSTD_e_end);
    }

    OperationStatus CompressCore(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, ZSTD_EndDirective endOperation)
    {
        var context = (ZSTD_CCtx_s*)handle;

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
            var remaining = ZSTD_compressStream2(context, &output, &input, endOperation);
            if (Zstandard.IsError(remaining))
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
    /// <exception cref="ZstandardException">Thrown when the reset operation fails.</exception>
    /// <remarks>
    /// This method resets only the session state, preserving all compression parameters.
    /// After calling Reset(), the encoder is ready to compress a new frame with the same settings.
    /// Any buffered data from the previous compression session is discarded.
    /// </remarks>
    public void Reset()
    {
        Validate();
        var context = (ZSTD_CCtx_s*)handle;

        var result = ZSTD_CCtx_reset(context, ZSTD_ResetDirective.ZSTD_reset_session_only);
        Zstandard.ThrowIfError(result);
    }

    public void Reset(in ZstandardCompressionOptions options)
    {
        Validate();
        var context = (ZSTD_CCtx_s*)handle;

        var result = ZSTD_CCtx_reset(context, ZSTD_ResetDirective.ZSTD_reset_session_and_parameters);
        Zstandard.ThrowIfError(result);

        options.SetParameter(context);
    }

    void Validate()
    {
        if (IsInvalid) Throws.InvalidContextNullException();
    }

    protected override bool ReleaseHandle()
    {
        ZSTD_freeCCtx((ZSTD_CCtx_s*)handle);
        handle = IntPtr.Zero;
        return true;
    }
}
