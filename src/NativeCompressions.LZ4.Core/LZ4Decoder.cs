using NativeCompressions.Internal;
using NativeCompressions.Interop;
using System.Buffers;
using System.Runtime.CompilerServices;
using static NativeCompressions.Interop.LZ4NativeMethods;

namespace NativeCompressions;

/// <summary>
/// Provides streaming decompression functionality for LZ4 Frame format.
/// This decoder supports incremental decompression with automatic frame header parsing.
/// </summary>
/// <remarks>
/// The decoder automatically handles frame headers, block headers, and validates checksums if present.
/// Call <see cref="Dispose"/> to release the native context. A finalizer releases it if Dispose is never called.
/// Instances are not thread-safe.
/// </remarks>
public sealed unsafe class LZ4Decoder : IDisposable
{
    // Held as a raw pointer instead of SafeHandle to keep a single managed allocation.
    // Released by Dispose or the finalizer.
    LZ4F_dctx_s* dctx;

    LZ4F_decompressOptions_t options;
    LZ4Dictionary? dictionary; // keeps the dictionary reachable while this decoder uses it

    /// <summary>
    /// Initializes a new instance of the <see cref="LZ4Decoder"/>.
    /// </summary>
    /// <exception cref="LZ4Exception">Thrown when the decompression context cannot be created.</exception>
    public LZ4Decoder()
        : this(LZ4DecompressionOptions.Default)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LZ4Decoder"/>.
    /// </summary>
    /// <exception cref="LZ4Exception">Thrown when the decompression context cannot be created.</exception>
    public LZ4Decoder(in LZ4DecompressionOptions options)
    {
        LZ4F_dctx_s* context = null;
        var code = LZ4F_createDecompressionContext(&context, LZ4.FrameVersion);
        LZ4.ThrowIfError(code);

        this.dctx = context;
        this.options = options.ToDecompressOptions();
        this.dictionary = options.Dictionary;
    }

    ~LZ4Decoder()
    {
        // Finalizer runs only when the object is unreachable, so no race with Dispose.
        var context = dctx;
        if (context != null)
        {
            dctx = null;
            LZ4F_freeDecompressionContext(context);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the decoder has been disposed.
    /// </summary>
    public bool IsDisposed => dctx == null;

    /// <summary>
    /// Determines the size of an LZ4 frame header from the beginning of a compressed stream.
    /// </summary>
    /// <param name="source">
    /// The beginning of a compressed LZ4 frame. Must be at least <see cref="LZ4.MinSizeToKnowFrameHeaderLength"/> bytes.
    /// </param>
    /// <returns>
    /// The size of the frame header in bytes (between 7 and 19 bytes for standard frames,
    /// or 8 bytes for skippable frames).
    /// </returns>
    /// <exception cref="LZ4Exception">
    /// Thrown when the source doesn't contain a valid LZ4 frame magic number,
    /// or when the source is too small to determine header size.
    /// </exception>
    public int GetHeaderSize(ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();

        fixed (byte* src = source)
        {
            var sizeOrErrorCode = LZ4F_headerSize(src, (nuint)source.Length);
            LZ4.ThrowIfError(sizeOrErrorCode);
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
    /// <exception cref="ObjectDisposedException">Thrown when the decoder has been disposed.</exception>
    /// <exception cref="LZ4Exception">
    /// Thrown when the source doesn't contain a valid LZ4 frame header,
    /// or when decompression context initialization fails.
    /// </exception>
    /// <remarks>
    /// This method serves two purposes: it extracts frame metadata from the header and it
    /// initializes the decompression context for subsequent <see cref="Decompress"/> calls.
    /// The bytes consumed should be skipped from the source when calling <see cref="Decompress"/>.
    /// </remarks>
    public LZ4FrameInfo GetFrameInfo(ReadOnlySpan<byte> source, out int bytesConsumed)
    {
        var context = GetContext();

        fixed (byte* src = source)
        {
            LZ4FrameInfo result = default;
            ref var frameInfo = ref Unsafe.As<LZ4FrameInfo, LZ4F_frameInfo_t>(ref result);

            var consumed = (nuint)source.Length;
            var hintOrErrorCode = LZ4F_getFrameInfo(context, (LZ4F_frameInfo_t*)Unsafe.AsPointer(ref frameInfo), src, &consumed);
            GC.KeepAlive(this);
            LZ4.ThrowIfError(hintOrErrorCode);

            bytesConsumed = (int)consumed;
            return result;
        }
    }

    /// <summary>
    /// Decompresses compressed data from the source buffer to the destination buffer.
    /// </summary>
    /// <param name="source">The compressed data to decompress. Can be partial frame data for streaming scenarios.</param>
    /// <param name="destination">The buffer to write decompressed data to.</param>
    /// <param name="bytesConsumed">When this method returns, contains the number of bytes consumed from the source buffer.</param>
    /// <param name="bytesWritten">When this method returns, contains the number of bytes written to the destination buffer.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> if the current frame is completely decompressed;
    /// <see cref="OperationStatus.NeedMoreData"/> if more compressed data is needed to continue;
    /// <see cref="OperationStatus.DestinationTooSmall"/> if the destination buffer is likely too small;
    /// <see cref="OperationStatus.InvalidData"/> if the data is invalid.
    /// </returns>
    /// <exception cref="ObjectDisposedException">Thrown when the decoder has been disposed.</exception>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
    {
        return Decompress(source, destination, out bytesConsumed, out bytesWritten, out _);
    }

    /// <summary>
    /// Decompresses compressed data from the source buffer to the destination buffer.
    /// </summary>
    /// <param name="source">The compressed data to decompress. Can be partial frame data for streaming scenarios.</param>
    /// <param name="destination">The buffer to write decompressed data to.</param>
    /// <param name="bytesConsumed">When this method returns, contains the number of bytes consumed from the source buffer.</param>
    /// <param name="bytesWritten">When this method returns, contains the number of bytes written to the destination buffer.</param>
    /// <param name="hintOfNextSrcSize">
    /// A hint of how many source bytes the next call expects, roughly the remaining compressed block plus the next block header.
    /// Respecting the hint skips intermediate buffers. Any source size is still accepted. 0 when the frame is complete or on error.
    /// </param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> if the current frame is completely decompressed;
    /// <see cref="OperationStatus.NeedMoreData"/> if more compressed data is needed to continue;
    /// <see cref="OperationStatus.DestinationTooSmall"/> if the destination buffer is likely too small;
    /// <see cref="OperationStatus.InvalidData"/> if the data is invalid.
    /// </returns>
    /// <exception cref="ObjectDisposedException">Thrown when the decoder has been disposed.</exception>
    /// <remarks>
    /// The decoder maintains internal state between calls. When <see cref="OperationStatus.Done"/> is returned,
    /// the frame is complete and the decoder is ready for the next frame.
    /// After <see cref="OperationStatus.InvalidData"/>, call <see cref="Reset"/> before reusing the decoder.
    /// The distinction between <see cref="OperationStatus.NeedMoreData"/> and <see cref="OperationStatus.DestinationTooSmall"/>
    /// is heuristic: a completely filled destination is reported as too small, otherwise more source is requested.
    /// </remarks>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, out int hintOfNextSrcSize)
    {
        var context = GetContext();

        fixed (byte* src = source)
        fixed (byte* dest = destination)
        fixed (LZ4F_decompressOptions_t* optionsPtr = &options)
        {
            var consumed = (nuint)source.Length;
            var written = (nuint)destination.Length;

            nuint hintOrErrorCode;
            if (dictionary == null)
            {
                hintOrErrorCode = LZ4F_decompress(context, dest, &written, src, &consumed, dOptPtr: optionsPtr);
            }
            else
            {
                // lz4frame stores this address at the first call and reads from it for every block of the frame,
                // so the dictionary keeps its bytes pinned rather than pinning them per call here
                hintOrErrorCode = LZ4F_decompress_usingDict(context, dest, &written, src, &consumed, dictionary.RawDictionaryPointer, (nuint)dictionary.RawDictionaryLength, decompressOptionsPtr: optionsPtr);
            }
            GC.KeepAlive(this);

            bytesConsumed = (int)consumed;
            bytesWritten = (int)written;

            if (LZ4.IsError(hintOrErrorCode))
            {
                hintOfNextSrcSize = 0; // the value is an error code, not a size
                return OperationStatus.InvalidData;
            }

            hintOfNextSrcSize = hintOrErrorCode > int.MaxValue ? int.MaxValue : (int)hintOrErrorCode;

            if (hintOrErrorCode == 0)
            {
                return OperationStatus.Done;
            }

            var sourceFullyConsumed = bytesConsumed == source.Length;
            var destinationFullyUsed = bytesWritten == destination.Length;

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

    /// <summary>
    /// Resets the decoder to start decoding a new frame, also after an error.
    /// </summary>
    public void Reset()
    {
        var context = GetContext();
        LZ4F_resetDecompressionContext(context);
        GC.KeepAlive(this);
    }

    /// <summary>
    /// Releases the native decompression context. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        // Interlocked has no pointer overload, so swap the field as an IntPtr while it is pinned.
        LZ4F_dctx_s* context;
        fixed (LZ4F_dctx_s** p = &dctx)
        {
            context = (LZ4F_dctx_s*)Interlocked.Exchange(ref *(IntPtr*)p, IntPtr.Zero);
        }

        if (context != null)
        {
            LZ4F_freeDecompressionContext(context);
        }
        GC.SuppressFinalize(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    LZ4F_dctx_s* GetContext()
    {
        var context = dctx;
        if (context == null) Throws.ObjectDisposedException(nameof(LZ4Decoder));
        return context;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void ThrowIfDisposed()
    {
        if (dctx == null) Throws.ObjectDisposedException(nameof(LZ4Decoder));
    }
}
