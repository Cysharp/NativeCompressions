using NativeCompressions.Internal;
using NativeCompressions.Interop;
using System.Runtime.CompilerServices;
using static NativeCompressions.Interop.LZ4NativeMethods;

namespace NativeCompressions;

// cctx = Compression Context
// dctx = Decompression Context
// CDict = Compression Dictionary

// BrotliEncoder interface is `public unsafe OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)`
// But LZ4F_compressUpdate() `When successful, the function always entirely consumes @srcBuffer.` so `out int bytesConsumed` is meaningless.
// When LZ4F_compressUpdate() has been failed, context state is broken so we need to throw error(can't impl Try... API).

/// <summary>
/// Provides streaming compression functionality for LZ4 Frame format.
/// This encoder supports incremental compression with automatic frame header generation.
/// </summary>
/// <remarks>
/// The encoder can be reused after calling <see cref="Close"/> to compress multiple frames sequentially.
/// Call <see cref="Dispose"/> to release the native context. A finalizer releases it if Dispose is never called.
/// Instances are not thread-safe.
/// </remarks>
public sealed unsafe class LZ4Encoder : IDisposable
{
    // Held as a raw pointer instead of SafeHandle to keep a single managed allocation.
    // Released by Dispose or the finalizer.
    LZ4F_cctx_s* cctx;

    LZ4F_preferences_t preferences;
    // The native context reads the dictionary whenever a frame begins. The encoder holds a lease on it
    // from the moment it is chosen until the context is freed.
    LZ4Dictionary.Lease dictionary;
    bool isWrittenHeader;

    /// <summary>
    /// Initializes a new instance of the <see cref="LZ4Encoder"/> with default settings.
    /// </summary>
    public LZ4Encoder()
        : this(LZ4CompressionOptions.Default)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LZ4Encoder"/> with specified options.
    /// </summary>
    /// <param name="options">Frame format options such as block size, compression level, and checksums. Pass LZ4CompressionOptions.Default for defaults.</param>
    /// <exception cref="LZ4Exception">Thrown when the compression context cannot be created.</exception>
    public LZ4Encoder(in LZ4CompressionOptions options)
    {
        LZ4F_cctx_s* context = null;
        var code = LZ4F_createCompressionContext(&context, LZ4.FrameVersion);
        LZ4.ThrowIfError(code);

        this.cctx = context;
        this.preferences = options.ToPreferences();

        try
        {
            this.dictionary = options.AcquireDictionary();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the encoder has been disposed.
    /// </summary>
    public bool IsDisposed => cctx == null;

    /// <summary>
    /// Calculates the maximum possible compressed size for the given input size.
    /// </summary>
    /// <param name="inputSize">Size of the uncompressed input data in bytes.</param>
    /// <param name="includingHeader">If true, includes the frame header sizes. Default is true.</param>
    /// <param name="includingFooter">If true, includes the frame footer sizes. Default is true.</param>
    /// <returns>Maximum possible size of compressed output in bytes (worst-case scenario).</returns>
    /// <remarks>
    /// This method returns the worst-case size assuming no compression.
    /// The actual compressed size is typically much smaller.
    /// Use this to allocate output buffers that are guaranteed to be large enough.
    /// When includingHeader or/and includingFooter is true (default), the returned size includes:
    /// - Frame header (up to 19 bytes)
    /// - Compressed data with block headers
    /// - Frame footer (4-8 bytes: end mark and optional content checksum)
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">inputSize is negative.</exception>
    /// <exception cref="OverflowException">The size does not fit in an int.</exception>
    public int GetMaxCompressedLength(int inputSize, bool includingHeader = true, bool includingFooter = true)
    {
        if (inputSize < 0) throw new ArgumentOutOfRangeException(nameof(inputSize));
        ThrowIfDisposed();

        nuint bound;
        fixed (LZ4F_preferences_t* prefs = &preferences)
        {
            bound = LZ4F_compressBound((nuint)inputSize, prefs);
        }

        // LZ4F_compressBegin requires room for the largest header, whatever size the header actually takes
        if (includingHeader) bound += (nuint)LZ4.MaxFrameHeaderLength;
        if (includingFooter) bound += (nuint)GetActualFrameFooterLength();
        return checked((int)bound);
    }

    /// <summary>
    /// Calculates the buffer size that is enough for <see cref="Flush"/>, or for <see cref="Close"/> when includingFooter is true.
    /// </summary>
    /// <param name="includingFooter">true to size the buffer for <see cref="Close"/>, false for <see cref="Flush"/>.</param>
    /// <remarks>
    /// The size for Close also has room for the frame header, because closing an encoder that has not
    /// compressed anything writes the header of an empty frame. So the value is enough for Close in any state.
    /// </remarks>
    public int GetMaxFlushBufferLength(bool includingFooter = false) => GetMaxCompressedLength(0, includingHeader: includingFooter, includingFooter: includingFooter);

    /// <summary>
    /// Gets the actual frame header size based on current options.
    /// </summary>
    /// <returns>Actual header size in bytes.</returns>
    public int GetActualFrameHeaderLength()
    {
        ThrowIfDisposed();

        int size = 7; // Base size (magic, FLG, BD, HC)

        if (preferences.frameInfo.contentSize > 0)
        {
            size += 8; // Content size field
        }

        if (preferences.frameInfo.dictID != 0)
        {
            size += 4; // Dictionary ID field
        }

        return size;
    }

    /// <summary>
    /// Gets the actual frame footer size based on current options.
    /// </summary>
    /// <returns>Actual footer size in bytes.</returns>
    public int GetActualFrameFooterLength()
    {
        ThrowIfDisposed();

        int size = 4; // End mark (always present)

        if (preferences.frameInfo.contentChecksumFlag == LZ4F_contentChecksum_t.LZ4F_contentChecksumEnabled)
        {
            size += 4; // Content checksum
        }

        return size;
    }

    /// <summary>
    /// Compresses source data and writes the result to the destination buffer.
    /// </summary>
    /// <param name="source">The data to compress. The entire source buffer will be consumed.</param>
    /// <param name="destination">The buffer to write compressed data to. Must be at least <see cref="GetMaxCompressedLength"/> in size.</param>
    /// <returns>The total number of bytes written to the destination buffer, including header (if first call).</returns>
    /// <exception cref="LZ4Exception">Thrown when compression fails (e.g., destination buffer too small).</exception>
    /// <remarks>
    /// On first call, automatically writes the LZ4 frame header.
    /// The source data is always entirely consumed - either compressed to destination or buffered internally.
    /// </remarks>
    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var context = GetContext();

        var totalWritten = 0;

        // Write header block
        if (!isWrittenHeader)
        {
            fixed (LZ4F_preferences_t* preference = &preferences)
            fixed (byte* dest = destination)
            {
                var writtenOrErrorCode = dictionary.IsEmpty
                    ? LZ4F_compressBegin(context, dest, (nuint)destination.Length, preference)
                    : LZ4F_compressBegin_usingCDict(context, dest, (nuint)destination.Length, dictionary.Compression, preference);
                LZ4.ThrowIfError(writtenOrErrorCode);
                isWrittenHeader = true;

                destination = destination.Slice((int)writtenOrErrorCode);
                totalWritten += (int)writtenOrErrorCode;
            }
        }

        // No input data, LZ4F_compressUpdate returns 0 so early return in C#.
        if (source.Length == 0)
        {
            GC.KeepAlive(this);
            return totalWritten;
        }

        // Write body
        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            // consume sources.
            var writtenOrErrorCode = LZ4F_compressUpdate(context, dest, (nuint)destination.Length, src, (nuint)source.Length, null);
            GC.KeepAlive(this);
            LZ4.ThrowIfError(writtenOrErrorCode);

            totalWritten += (int)writtenOrErrorCode; // written size can be zero, meaning input data was just buffered.
        }

        return totalWritten;
    }

    /// <summary>
    /// Flushes any buffered data to the destination buffer.
    /// </summary>
    /// <param name="destination">The buffer to write flushed data to.</param>
    /// <returns>The number of bytes written to the destination buffer. Returns 0 if no data was buffered.</returns>
    /// <exception cref="LZ4Exception">Thrown when flush operation fails.</exception>
    /// <remarks>
    /// Forces compression of any data buffered internally and writes it to the destination.
    /// This is useful when you need to ensure all input data has been processed and output,
    /// for example when streaming over a network.
    /// </remarks>
    public int Flush(Span<byte> destination)
    {
        var context = GetContext();

        fixed (byte* dest = destination)
        {
            // LZ4F_compressOptions_t(stableSrc) is currently not used in LZ4 source so always pass null.
            var writtenOrErrorCode = LZ4F_flush(context, dest, (nuint)destination.Length, cOptPtr: null);
            GC.KeepAlive(this);
            LZ4.ThrowIfError(writtenOrErrorCode);

            return (int)writtenOrErrorCode;
        }
    }

    /// <summary>
    /// Finalizes the current LZ4 frame by writing the ending marker and optional content checksum.
    /// </summary>
    /// <param name="destination">The buffer to write the frame ending to. It is guaranteed to be successful when destination.Length &gt;= GetMaxFlushBufferLength(includingFooter: true), which is the same as GetMaxCompressedLength(0).</param>
    /// <returns>The number of bytes written to the destination buffer (at least 4 bytes for the end marker).</returns>
    /// <exception cref="LZ4Exception">Thrown when finalization fails.</exception>
    /// <remarks>
    /// After calling this method, the encoder can be reused to compress another frame
    /// by calling Compress again (which will write a new header).
    /// </remarks>
    public int Close(Span<byte> destination)
    {
        var context = GetContext();

        var totalWritten = 0;
        if (!isWrittenHeader)
        {
            // This will write header, empty body.
            var written = Compress([], destination);
            destination = destination.Slice(written);
            totalWritten += written;
        }

        fixed (byte* dest = destination)
        {
            // LZ4F_compressOptions_t(stableSrc) is currently not used in LZ4 source so always pass null.
            var writtenOrErrorCode = LZ4F_compressEnd(context, dest, (nuint)destination.Length, cOptPtr: null);
            GC.KeepAlive(this);
            LZ4.ThrowIfError(writtenOrErrorCode);
            totalWritten += (int)writtenOrErrorCode;

            // A successful call to LZ4F_compressEnd() makes `cctx` available again for another compression task.
            isWrittenHeader = false;
        }

        return totalWritten;
    }

    /// <summary>
    /// Abandons the current frame, if one is in progress, so the next <see cref="Compress"/> starts a new frame with the same options.
    /// Data buffered for the abandoned frame is discarded.
    /// </summary>
    public void Reset()
    {
        DiscardFrame();
    }

    /// <summary>
    /// Abandons the current frame, if one is in progress, and applies new options. The next <see cref="Compress"/> starts a new frame with them.
    /// Data buffered for the abandoned frame is discarded.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the encoder has been disposed.</exception>
    /// <exception cref="LZ4Exception">Thrown when the context cannot begin a frame with the new options. The previous options stay in effect.</exception>
    public void Reset(in LZ4CompressionOptions options)
    {
        ThrowIfDisposed();

        var next = options.AcquireDictionary();
        var previousDictionary = dictionary;
        var previousPreferences = preferences;
        preferences = options.ToPreferences();
        dictionary = next;
        try
        {
            DiscardFrame(); // begins a frame with the new preferences and dictionary, which also drops buffered input
        }
        catch
        {
            preferences = previousPreferences;
            dictionary = previousDictionary;
            next.Dispose();
            isWrittenHeader = false; // the failed begin dropped the frame in progress, the next Compress begins again
            throw;
        }

        // the context no longer references the previous dictionary
        previousDictionary.Dispose();
    }

    // LZ4F_compressBegin reinitializes the context, which drops any input still buffered for the frame in progress.
    // The header it writes goes to a scratch buffer; the next Compress begins the frame again and writes the real one.
    void DiscardFrame()
    {
        // LZ4F compressionContext has no reset context(LZ4F_resetDecompressionContext is for decompressionContext) so we need to call LZ4F_compressBegin() to reset context.
        var context = GetContext();
        Span<byte> scratch = stackalloc byte[LZ4.MaxFrameHeaderLength];
        fixed (LZ4F_preferences_t* preference = &preferences)
        fixed (byte* dest = scratch)
        {
            var result = dictionary.IsEmpty
                ? LZ4F_compressBegin(context, dest, (nuint)scratch.Length, preference)
                : LZ4F_compressBegin_usingCDict(context, dest, (nuint)scratch.Length, dictionary.Compression, preference);
            GC.KeepAlive(this);
            LZ4.ThrowIfError(result);
        }
        isWrittenHeader = false;
    }

    ~LZ4Encoder()
    {
        // Finalizer runs only when the object is unreachable, so no race with Dispose.
        var context = cctx;
        if (context != null)
        {
            cctx = null;
            LZ4F_freeCompressionContext(context);
        }
        dictionary.Dispose();
    }

    /// <summary>
    /// Releases the native compression context. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        // Interlocked has no pointer overload, so swap the field as an IntPtr while it is pinned.
        LZ4F_cctx_s* context;
        fixed (LZ4F_cctx_s** p = &cctx)
        {
            context = (LZ4F_cctx_s*)Interlocked.Exchange(ref *(IntPtr*)p, IntPtr.Zero);
        }

        // Only the call that took the context releases the rest, so concurrent Dispose calls never release the lease twice.
        if (context != null)
        {
            LZ4F_freeCompressionContext(context);

            // freeing the context ends everything that reads the dictionary
            dictionary.Dispose();
            dictionary = default;
        }
        GC.SuppressFinalize(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    LZ4F_cctx_s* GetContext()
    {
        var context = cctx;
        if (context == null) Throws.ObjectDisposedException(nameof(LZ4Encoder));
        return context;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void ThrowIfDisposed()
    {
        if (cctx == null) Throws.ObjectDisposedException(nameof(LZ4Encoder));
    }
}
