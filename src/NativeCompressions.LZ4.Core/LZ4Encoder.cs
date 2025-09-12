using NativeCompressions.LZ4.Internal;
using NativeCompressions.LZ4.Raw;
using System.Runtime.CompilerServices;
using static NativeCompressions.LZ4.Raw.NativeMethods;

namespace NativeCompressions.LZ4;

// cctx = Compression Context
// dctx = Decompression Context
// CDict = Compression Dictionary

// Architecture Decision Record:
// BrotliEncoder interface is `public unsafe OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)`
// But LZ4F_compressUpdate() `When successful, the function always entirely consumes @srcBuffer.` so `out int bytesConsumed` is meaningless.
// When LZ4F_compressUpdate() has been failed, context state is broken so we need to throw error(can't impl Try... API).

/// <summary>
/// Provides streaming compression functionality for LZ4 Frame format.
/// This encoder supports incremental compression with automatic frame header generation.
/// </summary>
/// <remarks>
/// The encoder can be reused after calling <see cref="Close"/> to compress multiple frames sequentially.
/// Always dispose the encoder when finished to free unmanaged resources.
/// </remarks>
public unsafe partial struct LZ4Encoder : IDisposable
{
    LZ4F_cctx_s* context;
    LZ4FrameOptions header; // LZ4F_preferences_t
    LZ4CompressionDictionary? compressionDictionary;

    bool isWrittenHeader;
    bool disposed;

    public bool IsWriteHeader { get; set; } = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="LZ4Encoder"/> struct with default settings.
    /// </summary>
    public LZ4Encoder()
        : this(LZ4FrameOptions.Default, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LZ4Encoder"/> struct with specified options.
    /// </summary>
    /// <param name="frameOptions">Frame format options such as block size, compression level, and checksums. Pass LZ4FrameOptions.Default for defaults.</param>
    /// <param name="compressionDictionary">Optional pre-built dictionary for improved compression ratio. Pass null if not using dictionary compression.</param>
    /// <exception cref="LZ4Exception">Thrown when the compression context cannot be created.</exception>
    public LZ4Encoder(in LZ4FrameOptions frameOptions, LZ4CompressionDictionary? compressionDictionary = null)
    {
        // we hold handle in raw, does not wrap SafeHandle so be careful to use it.
        LZ4F_cctx_s* ptr = default;
        var code = LZ4F_createCompressionContext(&ptr, LZ4.FrameVersion);
        LZ4.ThrowIfError(code);
        this.context = ptr;
        this.header = frameOptions;
        this.compressionDictionary = compressionDictionary;
    }

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
    public unsafe int GetMaxCompressedLength(int inputSize, bool includingHeader = true, bool includingFooter = true)
    {
        var preferences = header.ToPreferences();
        var bound = (int)LZ4F_compressBound((nuint)inputSize, preferences);

        if (includingHeader && includingFooter)
        {
            return bound + GetActualFrameHeaderLength() + GetActualFrameFooterLength();
        }
        else if (includingHeader)
        {
            return bound + GetActualFrameHeaderLength();
        }
        else if (includingFooter)
        {
            return bound + GetActualFrameFooterLength();
        }
        else
        {
            return bound;
        }
    }

    public int GetMaxFlushBufferLength(bool includingFooter = false) => GetMaxCompressedLength(0, includingHeader: false, includingFooter: includingFooter);

    /// <summary>
    /// Gets the actual frame header size based on current options.
    /// </summary>
    /// <returns>Actual header size in bytes.</returns>
    public int GetActualFrameHeaderLength()
    {
        int size = 7; // Base size (magic, FLG, BD, HC)

        if (header.FrameInfo.ContentSize > 0)
        {
            size += 8; // Content size field
        }

        if (header.FrameInfo.DictionaryID != 0)
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
        int size = 4; // End mark (always present)

        if (header.FrameInfo.ContentChecksumFlag == ContentChecksum.ContentChecksumEnabled)
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
    /// <exception cref="ObjectDisposedException">Thrown when the encoder has been disposed.</exception>
    /// <exception cref="LZ4Exception">Thrown when compression fails (e.g., destination buffer too small).</exception>
    /// <remarks>
    /// On first call, automatically writes the LZ4 frame header.
    /// The source data is always entirely consumed - either compressed to destination or buffered internally.
    /// </remarks>
    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ValidateDisposed();

        var totalWritten = 0;

        // Write header block
        if (!isWrittenHeader)
        {
            fixed (byte* dest = destination)
            {
                var preferencePtr = header.ToPreferences();

                var writtenOrErrorCode = (compressionDictionary == null)
                    ? LZ4F_compressBegin(context, dest, (nuint)destination.Length, preferencePtr)
                    : LZ4F_compressBegin_usingCDict(context, dest, (nuint)destination.Length, compressionDictionary.Handle, preferencePtr);
                LZ4.ThrowIfError(writtenOrErrorCode);
                isWrittenHeader = true;

                // LZ4F_cctx_s always need to call compressBegin but header can ignore(write for single frame from multiple context(multiple block))
                if (IsWriteHeader)
                {
                    destination = destination.Slice((int)writtenOrErrorCode);
                    totalWritten += (int)writtenOrErrorCode;
                }
            }
        }

        // No input data, LZ4F_compressUpdate returns 0 so early return in C#.
        if (source.Length == 0)
        {
            return totalWritten;
        }

        // Write body
        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            // consume sources.
            var writtenOrErrorCode = LZ4F_compressUpdate(context, dest, (nuint)destination.Length, src, (nuint)source.Length, null);
            LZ4.ThrowIfError(writtenOrErrorCode);

            destination = destination.Slice((int)writtenOrErrorCode);
            totalWritten += (int)writtenOrErrorCode; // written size can be zero, meaning input data was just buffered.
        }

        return totalWritten;
    }

    /// <summary>
    /// Flushes any buffered data to the destination buffer.
    /// </summary>
    /// <param name="destination">The buffer to write flushed data to.</param>
    /// <returns>The number of bytes written to the destination buffer. Returns 0 if no data was buffered.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the encoder has been disposed.</exception>
    /// <exception cref="LZ4Exception">Thrown when flush operation fails.</exception>
    /// <remarks>
    /// Forces compression of any data buffered internally and writes it to the destination.
    /// This is useful when you need to ensure all input data has been processed and output,
    /// for example when streaming over a network.
    /// </remarks>
    public int Flush(Span<byte> destination)
    {
        ValidateDisposed();

        fixed (byte* dest = destination)
        {
            // LZ4F_compressOptions_t(stableSrc) is currently not used in LZ4 source so always pass null.
            var writtenOrErrorCode = LZ4F_flush(context, dest, (nuint)destination.Length, cOptPtr: null);
            LZ4.ThrowIfError(writtenOrErrorCode);

            return (int)writtenOrErrorCode;
        }
    }

    /// <summary>
    /// Finalizes the current LZ4 frame by writing the ending marker and optional content checksum.
    /// </summary>
    /// <param name="destination">The buffer to write the frame ending to. It is guaranteed to be successful when destination.Length &gt;= GetMaxCompressedLength(0).</param>
    /// <returns>The number of bytes written to the destination buffer (at least 4 bytes for the end marker).</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the encoder has been disposed.</exception>
    /// <exception cref="LZ4Exception">Thrown when finalization fails.</exception>
    /// <remarks>
    /// After calling this method, the encoder can be reused to compress another frame
    /// by calling Compress again (which will write a new header).
    /// </remarks>
    public int Close(Span<byte> destination)
    {
        ValidateDisposed();

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
            LZ4.ThrowIfError(writtenOrErrorCode);
            totalWritten += (int)writtenOrErrorCode;

            // secret option, LZ4Encoder can reuse after call Close()
            // beacuse: A successful call to LZ4F_compressEnd() makes `cctx` available again for another compression task.
            isWrittenHeader = false;
        }

        return totalWritten;
    }

    void ValidateDisposed()
    {
        if (disposed)
        {
            Throws.ObjectDisposedException();
        }
    }

    /// <summary>
    /// Sets the header options for the LZ4 frame.
    /// </summary>
    /// <param name="options">The LZ4 frame options to apply. Can be <see langword="null"/> to reset the header to its default state.</param>
    public void SetHeader(in LZ4FrameOptions options)
    {
        this.header = options;
    }

    /// <summary>
    /// Releases the unmanaged resources used by the <see cref="LZ4Encoder"/>.
    /// </summary>
    /// <remarks>
    /// Always call Dispose when finished with the encoder to free the compression context.
    /// It is safe to call Dispose multiple times.
    /// </remarks>
    public void Dispose()
    {
        if (!disposed)
        {
            // Note1 : LZ4F_freeCompressionContext() is always successful. Its return value can be ignored.
            // Note2 : LZ4F_freeCompressionContext() works fine with NULL input pointers (do nothing).
            LZ4F_freeCompressionContext(context);
            context = null;
            disposed = true;
        }
    }
}
