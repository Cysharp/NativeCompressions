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
/// </remarks>
public sealed unsafe class ZstandardDecoder : IDisposable
{
    // Held as a raw pointer instead of SafeHandle to keep a single managed allocation.
    // Released by Dispose or the finalizer.
    ZSTD_DCtx_s* dctx;

    // The native context only references the dictionary, so keep it reachable while this decoder is alive.
    ZstandardDictionary? dictionary;

    // Pinned prefix set by SetPrefix. zstd references the memory, so it stays pinned until Reset, Dispose or the next SetPrefix.
    MemoryHandle prefixHandle;
    bool hasPrefix;

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
        this.dctx = CreateContext();
        try
        {
            decompressionOptions.SetParameter(dctx);
            this.dictionary = decompressionOptions.Dictionary;
        }
        catch
        {
            Dispose();
            throw;
        }
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
    }

    /// <summary>
    /// Gets a value indicating whether the decoder has been disposed.
    /// </summary>
    public bool IsDisposed => dctx == null;

    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
    {
        return Decompress(source, destination, out bytesConsumed, out bytesWritten, out _);
    }

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
        var context = GetContext();

        var result = ZSTD_DCtx_reset(context, ZSTD_ResetDirective.ZSTD_reset_session_only);
        Zstandard.ThrowIfError(result);

        // A session reset keeps an unused prefix referenced by the native context, so drop that reference before unpinning.
        if (hasPrefix)
        {
            Zstandard.ThrowIfError(ZSTD_DCtx_refDDict(context, null));
            ReleasePrefix();
        }
        GC.KeepAlive(this);
    }

    public void Reset(in ZstandardDecompressionOptions options)
    {
        var context = GetContext();

        var result = ZSTD_DCtx_reset(context, ZSTD_ResetDirective.ZSTD_reset_session_and_parameters);
        Zstandard.ThrowIfError(result);
        dictionary = null;
        ReleasePrefix();

        options.SetParameter(context);
        dictionary = options.Dictionary;
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
        dictionary = null; // refPrefix clears the referenced dictionary
        GC.KeepAlive(this);
    }

    void ReleasePrefix()
    {
        prefixHandle.Dispose();
        prefixHandle = default;
        hasPrefix = false;
    }

    /// <summary>
    /// Releases the native decompression context. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        ReleasePrefix();
        // Interlocked has no pointer overload, so swap the field as an IntPtr while it is pinned.
        ZSTD_DCtx_s* context;
        fixed (ZSTD_DCtx_s** p = &dctx)
        {
            context = (ZSTD_DCtx_s*)Interlocked.Exchange(ref *(IntPtr*)p, IntPtr.Zero);
        }

        if (context != null)
        {
            ZSTD_freeDCtx(context);
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
