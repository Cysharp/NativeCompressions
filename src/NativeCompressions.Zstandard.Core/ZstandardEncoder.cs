using NativeCompressions.Internal;
using NativeCompressions.Interop;
using System.Buffers;
using System.Runtime.CompilerServices;
using static NativeCompressions.Interop.ZstandardNativeMethods;

namespace NativeCompressions;

/// <summary>
/// Provides streaming compression functionality for Zstandard format.
/// </summary>
/// <remarks>
/// Call <see cref="Dispose"/> to release the native context. A finalizer releases it if Dispose is never called.
/// Instances are not thread-safe.
/// </remarks>
public sealed unsafe class ZstandardEncoder : IDisposable
{
    // Held as a raw pointer instead of SafeHandle to keep a single managed allocation.
    // Released by Dispose or the finalizer.
    ZSTD_CCtx_s* cctx;

    // The native context only references the dictionary. The encoder holds a lease on it
    // from the moment the context refers to it until nothing native can read it any more.
    ZstandardDictionary.Lease dictionary;

    // Pinned prefix set by SetPrefix. zstd references the memory, so it stays pinned until Reset, Dispose or the next SetPrefix.
    MemoryHandle prefixHandle;
    bool hasPrefix;

    // What the native context is configured with. Kept to build a fresh context with the same parameters,
    // see ReplaceContext. SetPrefix removes the dictionary from it, the same as it does natively.
    ZstandardCompressionOptions options;

    // true from the first call of a frame until the frame is completely written
    bool frameInProgress;

    // Set by SetSourceLength for the next frame, -1 when nothing is declared.
    long declaredLength = -1;

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
        : this(compressionLevel == Zstandard.DefaultCompressionLevel ? ZstandardCompressionOptions.Default : new ZstandardCompressionOptions(compressionLevel))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZstandardEncoder"/> with specified options.
    /// </summary>
    public ZstandardEncoder(in ZstandardCompressionOptions compressionOptions)
    {
        this.cctx = CreateContext();
        try
        {
            this.dictionary = Configure(cctx, compressionOptions);
            this.options = compressionOptions;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Applies the options and returns the lease on their dictionary. On failure nothing is leased.
    static ZstandardDictionary.Lease Configure(ZSTD_CCtx_s* context, in ZstandardCompressionOptions options)
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
    /// Gets a value indicating whether the encoder has been disposed.
    /// </summary>
    public bool IsDisposed => cctx == null;

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
        var endOp = isFinalBlock ? ZSTD_EndDirective.ZSTD_e_end : ZSTD_EndDirective.ZSTD_e_continue;
        return CompressCore(source, destination, out bytesConsumed, out bytesWritten, endOp);
    }

    public OperationStatus Flush(Span<byte> destination, out int bytesWritten)
    {
        return CompressCore([], destination, out _, out bytesWritten, ZSTD_EndDirective.ZSTD_e_flush);
    }

    public OperationStatus Close(Span<byte> destination, out int bytesWritten)
    {
        return CompressCore([], destination, out _, out bytesWritten, ZSTD_EndDirective.ZSTD_e_end);
    }

    OperationStatus CompressCore(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, ZSTD_EndDirective endOperation)
    {
        var context = GetContext();

        // zstd takes the size from the input when a frame starts and ends in the same call, which would skip
        // the check of the declared length
        if (endOperation == ZSTD_EndDirective.ZSTD_e_end && !frameInProgress && declaredLength >= 0 && source.Length != declaredLength)
        {
            bytesWritten = 0;
            bytesConsumed = 0;
            return OperationStatus.InvalidData;
        }

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
            GC.KeepAlive(this); // keep the finalizer from freeing the context during the native call

            if (Zstandard.IsError(remaining))
            {
                frameInProgress = true; // the failed frame stays unfinished until Reset
                bytesWritten = 0;
                bytesConsumed = 0;
                return OperationStatus.InvalidData;
            }

            frameInProgress = !(endOperation == ZSTD_EndDirective.ZSTD_e_end && remaining == 0);
            if (!frameInProgress)
            {
                declaredLength = -1; // applies to one frame
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
                // Flush and Close promise that everything is written out, so data left in the internal buffer means "call again".
                // For continue it is normal for data to stay buffered.
                if (endOperation != ZSTD_EndDirective.ZSTD_e_continue && remaining > 0)
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
        var context = GetContext();

        var result = ZSTD_CCtx_reset(context, ZSTD_ResetDirective.ZSTD_reset_session_only);
        Zstandard.ThrowIfError(result);

        // A session reset keeps an unused prefix referenced by the native context, so drop that reference before unpinning.
        if (HasPrefix)
        {
            Zstandard.ThrowIfError(ZSTD_CCtx_refCDict(context, null));
        }

        ReleasePrefix();
        frameInProgress = false;
        declaredLength = -1;
        GC.KeepAlive(this);
    }

    public void Reset(in ZstandardCompressionOptions options)
    {
        var context = GetContext();

        ZstandardDictionary.Lease next;
        try
        {
            var result = ZSTD_CCtx_reset(context, ZSTD_ResetDirective.ZSTD_reset_session_and_parameters);
            Zstandard.ThrowIfError(result);
            next = Configure(context, options);
        }
        catch
        {
            // Some parameters are applied and some are not. The context is replaced by one with the previous
            // options, so the native parameters and the options kept here never disagree.
            RestoreOptions();
            throw;
        }

        // the reset dropped the reference to the previous dictionary
        dictionary.Dispose();
        dictionary = next;

        ReleasePrefix();
        this.options = options;
        frameInProgress = false;
        declaredLength = -1;
        GC.KeepAlive(this);
    }

    void RestoreOptions()
    {
        try
        {
            ReplaceContext(options);
        }
        catch
        {
            // the previous options cannot be applied any more, for example their dictionary was disposed
            options = ZstandardCompressionOptions.Default;
            ReplaceContext(options);
        }

        ReleasePrefix();
        frameInProgress = false;
        declaredLength = -1;
    }

    void ReplaceContext(in ZstandardCompressionOptions newOptions)
    {
        var fresh = CreateContext();
        ZstandardDictionary.Lease next;
        try
        {
            next = Configure(fresh, newOptions);
        }
        catch
        {
            ZSTD_freeCCtx(fresh);
            throw;
        }

        var old = cctx;
        cctx = fresh;
        ZSTD_freeCCtx(old);

        dictionary.Dispose();
        dictionary = next;
    }

    /// <summary>
    /// Declares the total size of the next frame so it is recorded in the frame header.
    /// Call before feeding any data of that frame. The value applies to the next frame only,
    /// and the frame fails to close if the actual size differs.
    /// </summary>
    public void SetSourceLength(long length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        var context = GetContext();

        var result = ZSTD_CCtx_setPledgedSrcSize(context, (ulong)length);
        GC.KeepAlive(this);
        Zstandard.ThrowIfError(result);
        declaredLength = length;
    }

    /// <summary>
    /// References a prefix that acts as a raw content dictionary for the next frame only.
    /// The decoder must be given the same prefix. Call before feeding any data of that frame.
    /// </summary>
    /// <remarks>
    /// The memory is pinned and must not be modified until the frame is reset, the encoder is disposed, or another prefix is set.
    /// Setting a prefix drops a dictionary given through options.
    /// </remarks>
    public void SetPrefix(ReadOnlyMemory<byte> prefix)
    {
        var context = GetContext();

        // Pin and register the new prefix first. If zstd rejects it (for example mid frame) the current prefix
        // stays referenced and pinned, so only the candidate is released.
        var handle = prefix.Pin();
        var result = ZSTD_CCtx_refPrefix(context, handle.Pointer, (nuint)prefix.Length);
        if (Zstandard.IsError(result))
        {
            handle.Dispose();
            Zstandard.ThrowIfError(result);
        }

        // zstd accepts a prefix only between frames, so nothing reads the previous prefix and dictionary any more
        ReleasePrefix();
        dictionary.Dispose();
        dictionary = default;

        prefixHandle = handle;
        hasPrefix = true;

        // refPrefix cleared the referenced dictionary, and it stays cleared after the frame.
        // The caller is free to dispose it from here on, so it must not be referenced again.
        if (options.Dictionary != null)
        {
            options = options with { Dictionary = null };
        }
        GC.KeepAlive(this);
    }

    bool HasPrefix => hasPrefix;

    void ReleasePrefix()
    {
        prefixHandle.Dispose();
        prefixHandle = default;
        hasPrefix = false;
    }

    ~ZstandardEncoder()
    {
        // Finalizer runs only when the object is unreachable, so no race with Dispose.
        var context = cctx;
        if (context != null)
        {
            cctx = null;
            ZSTD_freeCCtx(context);
        }
        prefixHandle.Dispose();
        dictionary.Dispose();
    }

    /// <summary>
    /// Releases the native compression context. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        // Interlocked has no pointer overload, so swap the field as an IntPtr while it is pinned.
        ZSTD_CCtx_s* context;
        fixed (ZSTD_CCtx_s** p = &cctx)
        {
            context = (ZSTD_CCtx_s*)Interlocked.Exchange(ref *(IntPtr*)p, IntPtr.Zero);
        }

        if (context != null)
        {
            ZSTD_freeCCtx(context);
        }

        // the context is gone, nothing reads the prefix and the dictionary after that
        ReleasePrefix();
        dictionary.Dispose();
        dictionary = default;
        GC.SuppressFinalize(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ZSTD_CCtx_s* GetContext()
    {
        var context = cctx;
        if (context == null) Throws.ObjectDisposedException(nameof(ZstandardEncoder));
        return context;
    }

    static ZSTD_CCtx_s* CreateContext()
    {
        var context = ZSTD_createCCtx();
        if (context == null) throw new ZstandardException("Failed to create compression context");
        return context;
    }
}
