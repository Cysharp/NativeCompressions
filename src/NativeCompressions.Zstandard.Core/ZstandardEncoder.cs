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

    // The native context only references the dictionary, so keep it reachable while this encoder is alive.
    ZstandardDictionary? dictionary;

    // Pinned prefix set by SetPrefix. zstd references the memory, so it stays pinned until Reset, Dispose or the next SetPrefix.
    MemoryHandle prefixHandle;
    bool hasPrefix;

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
    {
        this.cctx = CreateContext();

        if (compressionLevel != Zstandard.DefaultCompressionLevel)
        {
            try
            {
                var result = ZSTD_CCtx_setParameter(cctx, ZSTD_cParameter.ZSTD_c_compressionLevel, compressionLevel);
                Zstandard.ThrowIfError(result);
            }
            catch
            {
                Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZstandardEncoder"/> with specified options.
    /// </summary>
    public ZstandardEncoder(in ZstandardCompressionOptions compressionOptions)
    {
        this.cctx = CreateContext();
        try
        {
            compressionOptions.SetParameter(cctx);
            this.dictionary = compressionOptions.Dictionary;
        }
        catch
        {
            Dispose();
            throw;
        }
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
            ReleasePrefix();
        }
        GC.KeepAlive(this);
    }

    public void Reset(in ZstandardCompressionOptions options)
    {
        var context = GetContext();

        var result = ZSTD_CCtx_reset(context, ZSTD_ResetDirective.ZSTD_reset_session_and_parameters);
        Zstandard.ThrowIfError(result);
        dictionary = null;
        ReleasePrefix();

        options.SetParameter(context);
        dictionary = options.Dictionary;
        GC.KeepAlive(this);
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

        ReleasePrefix();
        prefixHandle = handle;
        hasPrefix = true;
        dictionary = null; // refPrefix clears the referenced dictionary
        GC.KeepAlive(this);
    }

    bool HasPrefix => hasPrefix;

    void ReleasePrefix()
    {
        prefixHandle.Dispose();
        prefixHandle = default;
        hasPrefix = false;
    }

    /// <summary>
    /// Releases the native compression context. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        ReleasePrefix();
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
