using NativeCompressions.Internal;
using NativeCompressions.Interop;
using System.Buffers;
using System.Runtime.CompilerServices;
using static NativeCompressions.Interop.ZstandardNativeMethods;

namespace NativeCompressions;

/// <summary>
/// Provides streaming decompression functionality for Zstandard format.
/// </summary>
/// <remarks>
/// Call <see cref="Dispose"/> to release the native context. A finalizer releases it if Dispose is never called.
/// Instances are not thread-safe.
/// After Decompress returns <see cref="OperationStatus.InvalidData"/>, call <see cref="Reset()"/> before reusing the decoder, or dispose it.
/// </remarks>
public sealed unsafe class ZstandardDecoder : IDisposable
{
    // Held as a raw pointer instead of SafeHandle to keep a single managed allocation.
    // Released by Dispose or the finalizer.
    ZSTD_DCtx_s* dctx;

    // The native context only references the dictionary. The decoder holds a lease on it
    // from the moment the context refers to it until the context lets go of it.
    ZstandardDictionary.Lease dictionary;

    // Pinned prefix set by SetPrefix. zstd references the memory, so it stays pinned until Reset, Dispose or the next SetPrefix.
    MemoryHandle prefixHandle;
    bool hasPrefix;

    bool frameInProgress;

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
    public ZstandardDecoder(in ZstandardDecompressionOptions options)
    {
        this.dctx = CreateContext();
        try
        {
            this.dictionary = Configure(dctx, options);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Applies the options and returns the lease on their dictionary. On failure nothing is leased.
    static ZstandardDictionary.Lease Configure(ZSTD_DCtx_s* context, in ZstandardDecompressionOptions options)
    {
        var lease = options.AcquireDictionary();
        try
        {
            options.SetParameter(context, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
        return lease;
    }

    /// <summary>
    /// Gets a value indicating whether the decoder has been disposed.
    /// </summary>
    public bool IsDisposed => dctx == null;

    // true once bytes of a frame were taken and until that frame completes or the decoder is reset
    internal bool IsFrameInProgress => frameInProgress;

    /// <inheritdoc cref="Decompress(ReadOnlySpan{byte}, Span{byte}, out int, out int, out int)"/>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
    {
        return Decompress(source, destination, out bytesConsumed, out bytesWritten, out _);
    }

    /// <summary>
    /// Decompresses source data and writes the result to the destination buffer.
    /// </summary>
    /// <param name="source">The compressed data. It may hold part of a frame.</param>
    /// <param name="destination">The buffer to write decompressed data to.</param>
    /// <param name="bytesConsumed">When this method returns, contains the number of bytes read from source.</param>
    /// <param name="bytesWritten">When this method returns, contains the number of bytes written to destination.</param>
    /// <param name="hintOfNextSrcSize">A hint of how many source bytes the next call expects. Any source size is still accepted. 0 when the frame is complete or on error.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> if the current frame is completely decompressed;
    /// <see cref="OperationStatus.NeedMoreData"/> if more compressed data is needed to continue;
    /// <see cref="OperationStatus.DestinationTooSmall"/> if output or input is left over, call again with more room;
    /// <see cref="OperationStatus.InvalidData"/> if the data is invalid.
    /// </returns>
    /// <exception cref="ObjectDisposedException">Thrown when the decoder has been disposed.</exception>
    /// <remarks>
    /// After <see cref="OperationStatus.InvalidData"/> the native context is in an error state, and what further calls do is undefined.
    /// Call <see cref="Reset()"/> before reusing the decoder, or dispose it.
    /// </remarks>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, out int hintOfNextSrcSize)
    {
        var context = GetContext();

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
            GC.KeepAlive(this); // keep the finalizer from freeing the context during the native call

            bytesConsumed = (int)input.pos;
            bytesWritten = (int)output.pos;

            if (Zstandard.IsError(hintOrErrorCode))
            {
                hintOfNextSrcSize = 0; // the value is an error code, not a size
                return OperationStatus.InvalidData;
            }

            hintOfNextSrcSize = hintOrErrorCode > int.MaxValue ? int.MaxValue : (int)hintOrErrorCode;

            if (hintOrErrorCode == 0)
            {
                frameInProgress = false;
                return OperationStatus.Done;
            }

            if (bytesConsumed > 0 || bytesWritten > 0) frameInProgress = true;

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

    /// <summary>
    /// Abandons the current frame, if one is in progress, so the next Decompress starts a new frame with the same options.
    /// Required after Decompress returned <see cref="OperationStatus.InvalidData"/>.
    /// </summary>
    public void Reset()
    {
        var context = GetContext();

        var result = ZSTD_DCtx_reset(context, ZSTD_ResetDirective.ZSTD_reset_session_only);
        Zstandard.ThrowIfError(result);
        frameInProgress = false;

        // A session reset keeps an unused prefix referenced by the native context, so drop that reference before unpinning.
        if (hasPrefix)
        {
            Zstandard.ThrowIfError(ZSTD_DCtx_refDDict(context, null));
            ReleasePrefix();
        }
        GC.KeepAlive(this);
    }

    /// <summary>
    /// Abandons the current frame, if one is in progress, and applies new options. The next Decompress starts a new frame with them.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the decoder has been disposed.</exception>
    /// <exception cref="ZstandardException">Thrown when the reset fails or the new options cannot be applied.</exception>
    public void Reset(in ZstandardDecompressionOptions options)
    {
        var context = GetContext();

        var result = ZSTD_DCtx_reset(context, ZSTD_ResetDirective.ZSTD_reset_session_and_parameters);
        Zstandard.ThrowIfError(result);
        frameInProgress = false;
        ReleasePrefix();

        // the reset dropped the native reference to the previous dictionary, whether or not the new options apply
        var previous = dictionary;
        dictionary = default;
        try
        {
            dictionary = Configure(context, options);
        }
        finally
        {
            previous.Dispose();
        }
        GC.KeepAlive(this);
    }

    /// <summary>
    /// References the prefix the encoder used for the next frame only. Call before feeding any data of that frame.
    /// </summary>
    /// <remarks>
    /// The memory is pinned and must not be modified until the frame is reset, the decoder is disposed, or another prefix is set.
    /// Setting a prefix drops a dictionary given through options.
    /// </remarks>
    public void SetPrefix(ReadOnlyMemory<byte> prefix)
    {
        var context = GetContext();

        // Pin and register the new prefix first. If zstd rejects it (for example mid frame) the current prefix
        // stays referenced and pinned, so only the candidate is released.
        var handle = prefix.Pin();
        var result = ZSTD_DCtx_refPrefix(context, handle.Pointer, (nuint)prefix.Length);
        if (Zstandard.IsError(result))
        {
            handle.Dispose();
            Zstandard.ThrowIfError(result);
        }

        ReleasePrefix();
        prefixHandle = handle;
        hasPrefix = true;
        dictionary.Dispose(); // refPrefix replaced the referenced dictionary
        dictionary = default;
        GC.KeepAlive(this);
    }

    void ReleasePrefix()
    {
        prefixHandle.Dispose();
        prefixHandle = default;
        hasPrefix = false;
    }

    ~ZstandardDecoder()
    {
        // Finalizer runs only when the object is unreachable, so no race with Dispose.
        var context = dctx;
        if (context != null)
        {
            dctx = null;
            ZSTD_freeDCtx(context);
        }
        prefixHandle.Dispose();
        dictionary.Dispose();
    }

    /// <summary>
    /// Releases the native decompression context. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        // Interlocked has no pointer overload, so swap the field as an IntPtr while it is pinned.
        ZSTD_DCtx_s* context;
        fixed (ZSTD_DCtx_s** p = &dctx)
        {
            context = (ZSTD_DCtx_s*)Interlocked.Exchange(ref *(IntPtr*)p, IntPtr.Zero);
        }

        // Only the call that took the context releases the rest, so concurrent Dispose calls never release the lease or the prefix twice.
        if (context != null)
        {
            ZSTD_freeDCtx(context);

            // freeing the context ends everything that reads the prefix and the dictionary
            ReleasePrefix();
            dictionary.Dispose();
            dictionary = default;
        }
        GC.SuppressFinalize(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ZSTD_DCtx_s* GetContext()
    {
        var context = dctx;
        if (context == null) Throws.ObjectDisposedException(nameof(ZstandardDecoder));
        return context;
    }

    static ZSTD_DCtx_s* CreateContext()
    {
        var context = ZSTD_createDCtx();
        if (context == null) throw new ZstandardException("Failed to create decompression context");
        return context;
    }
}
