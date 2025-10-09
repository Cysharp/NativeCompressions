using NativeCompressions.Internal;
using NativeCompressions.Interop;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static NativeCompressions.Interop.ZstandardNativeMethods;

namespace NativeCompressions;

/// <summary>
/// Provides streaming decompression functionality for Zstandard format.
/// </summary>
public unsafe struct ZstandardDecoder : IDisposable
{
    // native context in SafeHandle(for safety) and all struct fields in heap(for struct small size)
    ZstandardDecoderState? state;

    /// <summary>
    /// Initializes a new instance of the <see cref="ZstandardDecoder"/>.
    /// </summary>
    public ZstandardDecoder()
        : this(ZstandardDecompressionOptions.Default)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZstandardDecoder"/> with specified options.
    /// </summary>
    public ZstandardDecoder(in ZstandardDecompressionOptions decompressionOptions)
    {
        this.state = new ZstandardDecoderState();
        try
        {
            decompressionOptions.SetParameter(state.DangerousGetHandle());
        }
        catch
        {
            this.state.Dispose();
            throw;
        }
    }

    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
    {
        return Decompress(source, destination, out bytesConsumed, out bytesWritten, out _);
    }

    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, out int hintOfNextSrcSize)
    {
        Validate();
        var context = state.DangerousGetHandle();

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
        Validate();
        var context = state.DangerousGetHandle();

        var result = ZSTD_DCtx_reset(context, ZSTD_ResetDirective.ZSTD_reset_session_only);
        Zstandard.ThrowIfError(result);
    }

    public void Reset(in ZstandardDecompressionOptions options)
    {
        Validate();
        var context = state.DangerousGetHandle();

        var result = ZSTD_DCtx_reset(context, ZSTD_ResetDirective.ZSTD_reset_session_and_parameters);
        Zstandard.ThrowIfError(result);

        options.SetParameter(context);
    }

    public void Dispose()
    {
        if (state == null) return;
        state.Dispose();
    }

    [MemberNotNull(nameof(state))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Validate()
    {
        if (state == null) Throws.InvalidContextNullException();
        if (state.IsClosed) Throws.ObjectDisposedException();
    }

    unsafe class ZstandardDecoderState : SafeHandle
    {
        public override bool IsInvalid => handle == IntPtr.Zero;

        public new ZSTD_DCtx_s* DangerousGetHandle() => (ZSTD_DCtx_s*)handle;

        public ZstandardDecoderState()
           : base(IntPtr.Zero, true)
        {
            var context = ZSTD_createDCtx();
            if (context == null) throw new ZstandardException("Failed to create decompression context");

            this.handle = (IntPtr)context; // assign to SafeHandle
        }

        protected override bool ReleaseHandle()
        {
            ZSTD_freeDCtx((ZSTD_DCtx_s*)handle);
            handle = IntPtr.Zero;
            return true;
        }
    }
}
