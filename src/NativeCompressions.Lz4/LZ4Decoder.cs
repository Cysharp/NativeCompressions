using NativeCompressions.LZ4.Internal;
using NativeCompressions.LZ4.Raw;
using System.Buffers;
using System.Runtime.CompilerServices;
using static NativeCompressions.LZ4.Raw.NativeMethods;

namespace NativeCompressions.LZ4;

/// <summary>
/// Provides streaming decompression functionality for LZ4 Frame format.
/// This decoder supports incremental decompression with automatic frame header parsing.
/// </summary>
/// <remarks>
/// The decoder automatically handles frame headers, block headers, and validates checksums if present.
/// It can decompress data incrementally, making it suitable for streaming scenarios.
/// </remarks>
public unsafe partial struct LZ4Decoder : IDisposable
{
    LZ4F_dctx_s* context;
    bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LZ4Decoder"/> struct.
    /// </summary>
    /// <exception cref="LZ4Exception">Thrown when the decompression context cannot be created.</exception>
    public LZ4Decoder()
    {
        // we hold handle in raw, does not wrap SafeHandle so be careful to use it.
        LZ4F_dctx_s* ptr = default;
        var code = LZ4F_createDecompressionContext(&ptr, LZ4.FrameVersion);
        LZ4.HandleErrorCode(code);

        this.context = ptr;
        this.disposed = false;
    }

    /// <summary>
    /// Gets the minimum number of bytes required to determine the LZ4 frame header size.
    /// </summary>
    /// <returns>The minimum bytes needed (5 bytes) to identify header size.</returns>
    /// <remarks>
    /// This is the smallest amount of data needed to parse the frame's magic number
    /// and flags to determine the full header size. Use this value to ensure you have
    /// enough data before calling <see cref="GetHeaderSize"/>.
    /// </remarks>
    public int GetMinSizeToKnowHeaderLength() => 5; // LZ4F_MIN_SIZE_TO_KNOW_HEADER_LENGTH

    /// <summary>
    /// Determines the size of an LZ4 frame header from the beginning of a compressed stream.
    /// </summary>
    /// <param name="source">
    /// The beginning of a compressed LZ4 frame. Must be at least <see cref="GetMinSizeToKnowHeaderLength"/> bytes.
    /// </param>
    /// <returns>
    /// The size of the frame header in bytes (between 7 and 19 bytes for standard frames,
    /// or 8 bytes for skippable frames).
    /// </returns>
    /// <exception cref="LZ4Exception">
    /// Thrown when the source doesn't contain a valid LZ4 frame magic number,
    /// or when the source is too small to determine header size.
    /// </exception>
    /// <remarks>
    /// Call this method when you need to know how much data to read for the complete header.
    /// The actual header size depends on which optional fields are present:
    /// - Base header: 7 bytes (magic number, flags, block descriptor)
    /// - Content size field: +8 bytes (if enabled)
    /// - Dictionary ID: +4 bytes (if present)
    /// 
    /// For skippable frames, the header is always 8 bytes.
    /// </remarks>
    public int GetHeaderSize(ReadOnlySpan<byte> source)
    {
        ValidateDisposed();

        fixed (byte* src = source)
        {
            var sizeOrErrorCode = LZ4F_headerSize(src, (nuint)source.Length);
            LZ4.HandleErrorCode(sizeOrErrorCode);
            return (int)sizeOrErrorCode;
        }
    }
    /// <summary>
    /// Extracts frame information from an LZ4 frame header and initializes the decompression context.
    /// </summary>
    /// <param name="source">
    /// The compressed data containing at least the complete frame header.
    /// Must be at least as large as the size returned by <see cref="GetHeaderSize"/>.
    /// </param>
    /// <param name="bytesConsumed">
    /// When this method returns, contains the number of bytes consumed from the source
    /// to parse the frame header.
    /// </param>
    /// <returns>
    /// The frame information extracted from the header, including block size,
    /// content size (if present), checksum flags, and other frame parameters.
    /// </returns>
    /// <exception cref="ObjectDisposedException">
    /// Thrown when the decoder has been disposed.
    /// </exception>
    /// <exception cref="LZ4Exception">
    /// Thrown when the source doesn't contain a valid LZ4 frame header,
    /// or when decompression context initialization fails.
    /// </exception>
    /// <remarks>
    /// This method serves two purposes:
    /// 1. Extracts frame metadata from the header
    /// 2. Initializes the decompression context for subsequent <see cref="Decompress"/> calls
    /// 
    /// After calling this method, the decoder is ready to decompress the frame body.
    /// The bytes consumed should be skipped from the source when calling <see cref="Decompress"/>.
    /// 
    /// If the frame header specifies a content size, it will be available in the returned
    /// <see cref="LZ4FrameInfo.ContentSize"/> property, which can be used to pre-allocate
    /// the exact output buffer size.
    /// </remarks>
    public LZ4FrameInfo GetFrameInfo(ReadOnlySpan<byte> source, out int bytesConsumed)
    {
        ValidateDisposed();

        fixed (byte* src = source)
        {
            LZ4FrameInfo result = default;
            ref var frameInfo = ref Unsafe.As<LZ4FrameInfo, LZ4F_frameInfo_t>(ref result);

            var consumed = (nuint)source.Length;
            var hintOrErrorCode = LZ4F_getFrameInfo(context, (LZ4F_frameInfo_t*)Unsafe.AsPointer(ref frameInfo), src, &consumed);
            LZ4.HandleErrorCode(hintOrErrorCode);

            bytesConsumed = (int)consumed;
            return result;
        }
    }

    /// <summary>
    /// Decompresses compressed data from the source buffer to the destination buffer.
    /// </summary>
    /// <param name="source">
    /// The compressed data to decompress. Can be partial frame data for streaming scenarios.
    /// </param>
    /// <param name="destination">
    /// The buffer to write decompressed data to. Must be large enough to hold the decompressed output.
    /// </param>
    /// <param name="bytesConsumed">
    /// When this method returns, contains the number of bytes consumed from the source buffer.
    /// </param>
    /// <param name="bytesWritten">
    /// When this method returns, contains the number of bytes written to the destination buffer.
    /// </param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> if the current frame is completely decompressed;
    /// <see cref="OperationStatus.NeedMoreData"/> if more compressed data is needed to continue;
    /// <see cref="OperationStatus.DestinationTooSmall"/> if the destination buffer is likely too small.
    /// </returns>
    /// <exception cref="ObjectDisposedException">
    /// Thrown when the decoder has been disposed.
    /// </exception>
    /// <exception cref="LZ4Exception">
    /// Thrown when decompression fails due to data corruption, invalid format, or other errors.
    /// After this exception, the decoder is in an undefined state and must be disposed and recreated.
    /// </exception>
    /// <remarks>
    /// This method supports streaming decompression and can be called multiple times with sequential
    /// chunks of compressed data. The decoder maintains internal state between calls.
    /// 
    /// When <see cref="OperationStatus.Done"/> is returned, the current frame has been completely
    /// decompressed and the decoder is ready to process a new frame.
    /// 
    /// The distinction between <see cref="OperationStatus.NeedMoreData"/> and 
    /// <see cref="OperationStatus.DestinationTooSmall"/> is heuristic-based:
    /// - If the destination buffer was completely filled, it's likely too small
    /// - Otherwise, more source data is needed
    /// 
    /// For optimal performance, provide destination buffers of at least 64KB or use
    /// the content size from <see cref="GetFrameInfo"/> if available.
    /// </remarks>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
    {
        ValidateDisposed();

        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            var consumed = (nuint)source.Length;
            var written = (nuint)destination.Length;

            var hintOrErrorCode = LZ4F_decompress(context, dest, &written, src, &consumed, null);
            LZ4.HandleErrorCode(hintOrErrorCode);

            bytesConsumed = (int)consumed;
            bytesWritten = (int)written;

            if (hintOrErrorCode == 0)
            {
                return OperationStatus.Done;
            }

            // Heuristic to distinguish between "need more data" vs "destination too small"
            // If destination was completely filled, it's likely too small
            if (bytesWritten == destination.Length && bytesWritten > 0)
            {
                return OperationStatus.DestinationTooSmall;
            }
            else
            {
                // Source was exhausted or no output was produced
                return OperationStatus.NeedMoreData;
            }
        }
    }

    /// <summary>
    /// Resets the decoder state to prepare for decompressing a new frame.
    /// </summary>
    /// <exception cref="ObjectDisposedException">
    /// Thrown when the decoder has been disposed.
    /// </exception>
    /// <remarks>
    /// This method clears the internal state and prepares the decoder for a new frame.
    /// It is automatically called internally when a frame is completely decompressed
    /// (when <see cref="Decompress"/> returns <see cref="OperationStatus.Done"/>).
    /// 
    /// You may call this method explicitly in the following scenarios:
    /// - To abandon decompression of the current frame and start processing a new one
    /// - To clear internal buffers and reduce memory usage between frames
    /// - When switching between different compressed streams
    /// 
    /// Note: This method does NOT recover from decompression errors. If <see cref="Decompress"/>
    /// throws an exception due to corrupted data, the decoder must be disposed and recreated,
    /// not reset.
    /// </remarks>
    public void Reset()
    {
        ValidateDisposed();
        LZ4F_resetDecompressionContext(context);
    }

    void ValidateDisposed()
    {
        if (disposed)
        {
            Throws.ObjectDisposedException();
        }
    }

    /// <summary>
    /// Releases the unmanaged resources used by the <see cref="LZ4Decoder"/>.
    /// </summary>
    /// <remarks>
    /// Always call Dispose when finished with the decoder to free the decompression context.
    /// It is safe to call Dispose multiple times.
    /// </remarks>
    public void Dispose()
    {
        if (context != null)
        {
            // LZ4F_freeDecompressionContext always returns success, no need to check
            LZ4F_freeDecompressionContext(context);
            context = null;
            disposed = true;
        }
    }
}
