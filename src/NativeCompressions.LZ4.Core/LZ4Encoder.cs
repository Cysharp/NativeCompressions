using NativeCompressions.Internal;
using NativeCompressions.Interop;
using System.Buffers;
using System.Runtime.CompilerServices;
using static NativeCompressions.Interop.LZ4NativeMethods;

namespace NativeCompressions;

// cctx = Compression Context
// dctx = Decompression Context
// CDict = Compression Dictionary

// LZ4F_compressUpdate always consumes its whole input and fails when its output does not fit, which also breaks the frame.
// So Compress passes only as much input as is sure to fit, assuming every block is stored uncompressed.
// LZ4F_flush and LZ4F_compressEnd keep what does not fit and continue on the next call, so Flush and Close pass any room.
// A destination smaller than a block gets the buffered block through LZ4F_flush, which keeps the rest the same way.

/// <summary>
/// Provides streaming compression functionality for LZ4 Frame format.
/// This encoder supports incremental compression with automatic frame header generation.
/// </summary>
/// <remarks>
/// The encoder can be reused after <see cref="Close"/> returns <see cref="OperationStatus.Done"/> to compress multiple frames sequentially.
/// Call <see cref="Dispose"/> to release the native context. A finalizer releases it if Dispose is never called.
/// Instances are not thread-safe.
/// After Compress, Flush or Close returns <see cref="OperationStatus.InvalidData"/>, call <see cref="Reset()"/> before reusing the encoder, or dispose it.
/// Every call makes progress when the destination has at least <see cref="LZ4.MaxFrameHeaderLength"/> bytes.
/// While a Compress with isFinalBlock has not taken the whole source, call it again with the rest of the source as usual.
/// Once the frame is being ended, that is Close returned <see cref="OperationStatus.DestinationTooSmall"/>, or Compress with isFinalBlock did after
/// taking the whole source, only the rest of the frame can follow: Close, or Compress with an empty source and isFinalBlock.
/// New input or a Flush then returns <see cref="OperationStatus.InvalidData"/>.
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

    FrameState state;

    // What lz4 holds back of an unfinished block. It emits whole blocks only, so after LZ4F_compressUpdate this is
    // (held + input) % blockSize, and nothing after a frame begins or a flush completes. Always 0 with autoFlush.
    int buffered;

    // Set when the frame starts to end, by Close or by a final Compress that took its whole source, until the frame is completely written.
    // Only the rest of the frame can follow meanwhile.
    bool closing;

    // what is left of the frame ending while the state is Ending
    int footerLeft;

    enum FrameState : byte
    {
        Idle,     // no frame, the next call begins one
        Open,     // the header is written and input is taken
        Flushing, // lz4 holds output of a flush that did not fit
        Ending,   // everything buffered is out and the frame ending is being written
        Faulted,  // lz4 failed, the frame cannot go on until Reset
    }

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
    /// <exception cref="LZ4Exception">Thrown when the compression context cannot be created, or lz4 rejects the options.</exception>
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
            ValidateFrame(); // the same check as Reset(options), so bad options fail here like they do for ZstandardEncoder
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
    /// A destination of this size lets <see cref="Compress"/> take the whole input in one call. With includingFooter (default),
    /// it also lets the same call end the frame with isFinalBlock, or a following <see cref="Close"/> finish into the rest of it,
    /// including what the encoder still holds from earlier calls. For a call that does not end the frame, includingFooter: false is enough and smaller.
    /// After a <see cref="Flush"/> that returned <see cref="OperationStatus.DestinationTooSmall"/>, its remaining output comes first and may need another call.
    /// When includingHeader or/and includingFooter is true (default), the returned size includes:
    /// - Frame header (7 to 19 bytes, depending on the options)
    /// - Compressed data with block headers
    /// - Frame footer (4-8 bytes: end mark and optional content checksum), and the last block of what the encoder holds
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">inputSize is negative.</exception>
    /// <exception cref="OverflowException">The size does not fit in an int.</exception>
    public int GetMaxCompressedLength(int inputSize, bool includingHeader = true, bool includingFooter = true)
    {
        if (inputSize < 0) throw new ArgumentOutOfRangeException(nameof(inputSize));
        ThrowIfDisposed();

        // what one LZ4F_compressUpdate can write, when up to a block of earlier input is already held
        ulong bound;
        fixed (LZ4F_preferences_t* prefs = &preferences)
        {
            bound = LZ4F_compressBound((nuint)inputSize, prefs);
        }

        if (includingFooter && preferences.autoFlush == 0)
        {
            // Ending the frame also writes out what lz4 holds, up to a block less a byte, as the last blocks together with this input.
            // LZ4F_compressBound leaves that to a separate LZ4F_compressEnd, so the whole of it is counted here, every block stored uncompressed.
            ulong blockSize = (ulong)BlockSize;
            ulong total = (ulong)inputSize + blockSize - 1;
            ulong blocks = (total + blockSize - 1) / blockSize;
            bound = Math.Max(bound, total + blocks * (ulong)BlockOverhead);
        }

        if (includingHeader) bound += (ulong)GetActualFrameHeaderLength();
        if (includingFooter) bound += (ulong)GetActualFrameFooterLength();
        return checked((int)bound);
    }

    /// <summary>
    /// Calculates the buffer size that lets <see cref="Flush"/> and <see cref="Close"/> finish in one call.
    /// </summary>
    /// <param name="includingFooter">
    /// true (default) to size the buffer for <see cref="Close"/>, which is also enough for <see cref="Flush"/>.
    /// false to size it for <see cref="Flush"/> only, saving the room for the frame header and footer.
    /// </param>
    /// <remarks>
    /// The size for Close also has room for the frame header, because closing an encoder that has not
    /// compressed anything writes the header of an empty frame. So the default value is the same as
    /// <see cref="GetMaxCompressedLength"/> with an inputSize of 0.
    /// Output that an earlier call could not write out comes first and may need another call.
    /// </remarks>
    public int GetMaxFlushBufferLength(bool includingFooter = true) => GetMaxCompressedLength(0, includingHeader: includingFooter, includingFooter: includingFooter);

    // Room for a whole block with the frame header and ending. Compress then makes full blocks, and Flush and Close finish in one call.
    // Without autoFlush it equals GetMaxFlushBufferLength, LZ4F_compressBound(0) is a block, its header and the frame ending,
    // and with autoFlush it is larger. Computed here, so LZ4Stream asks lz4 only once per write.
    internal int GetBlockBufferLength() => BlockSize + BlockOverhead + GetActualFrameHeaderLength() + 2 * GetActualFrameFooterLength();

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
    /// <param name="source">The data to compress. May be empty.</param>
    /// <param name="destination">The buffer to write compressed data to.</param>
    /// <param name="bytesConsumed">When this method returns, contains the number of bytes read from source.</param>
    /// <param name="bytesWritten">When this method returns, contains the number of bytes written to destination.</param>
    /// <param name="isFinalBlock">true to end the frame after the source; false to continue streaming.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/>: All input was consumed. If isFinalBlock is true, the frame is also completely written and the encoder can start another frame.
    /// <see cref="OperationStatus.DestinationTooSmall"/>: The rest of the source or of the output needs more room, which can happen before the destination is full,
    /// because the encoder takes only as much input as surely fits. Call again with the rest of the source and more room.
    /// <see cref="OperationStatus.InvalidData"/>: The compression failed, for example the frame length differs from ContentSize. bytesConsumed and bytesWritten are 0.
    /// </returns>
    /// <exception cref="ObjectDisposedException">Thrown when the encoder has been disposed.</exception>
    /// <remarks>
    /// This method follows the same pattern as System.IO.Compression.BrotliEncoder.
    /// The first input of a frame comes after the frame header, an empty source before it writes nothing. Input can stay buffered in the encoder
    /// until a block is full, <see cref="Flush"/> writes it out. A destination of <see cref="GetMaxCompressedLength"/> for the source length
    /// takes the whole source in one call, and with isFinalBlock also ends the frame in it.
    /// A destination smaller than <see cref="LZ4.MaxFrameHeaderLength"/> may get nothing, then call again with more room.
    /// After <see cref="OperationStatus.InvalidData"/>, call <see cref="Reset()"/> before reusing the encoder, or dispose it.
    /// </remarks>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)
    {
        var status = CompressCore(source, destination, out bytesConsumed, out bytesWritten, isFinalBlock);
        if (status == OperationStatus.InvalidData)
        {
            // nothing of a broken frame counts, the same as ZstandardEncoder
            bytesConsumed = 0;
            bytesWritten = 0;
        }
        return status;
    }

    OperationStatus CompressCore(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)
    {
        var context = GetContext();
        bytesConsumed = 0;
        bytesWritten = 0;

        if (closing)
        {
            // only the rest of the frame can follow while it is being ended
            return source.IsEmpty && isFinalBlock ? CloseCore(context, destination, ref bytesWritten) : Fault();
        }

        // the frame begins with its first input, so an empty source has nothing to do yet
        if (state == FrameState.Idle && source.IsEmpty && !isFinalBlock) return OperationStatus.Done;

        var status = Prepare(context, destination, ref bytesWritten);
        if (status != OperationStatus.Done) return status;

        while (bytesConsumed < source.Length)
        {
            var room = destination.Length - bytesWritten;
            var remaining = source.Length - bytesConsumed;
            var fits = GetMaxSourceLength(room);
            if (fits > 0)
            {
                var count = Math.Min(remaining, fits);
                if (count < remaining)
                {
                    // A part of the source ends at a block boundary when it can. Without autoFlush lz4 then compresses it in place
                    // instead of copying the rest of a block aside, and with autoFlush, where every call ends a block, the blocks stay
                    // the ones one call makes. With autoFlush less than a block goes in only while the destination has taken nothing else.
                    var aligned = count - (int)((buffered + (long)count) % BlockSize);
                    if (aligned > 0) count = aligned;
                    else if (preferences.autoFlush != 0 && bytesWritten > 0) return OperationStatus.DestinationTooSmall;
                }
                if (!Update(context, source.Slice(bytesConsumed, count), destination.Slice(bytesWritten), out var written)) return Fault();
                bytesConsumed += count;
                bytesWritten += written;
            }
            else if (bytesWritten > 0 || room == 0 || preferences.autoFlush != 0)
            {
                // the rest needs a fresh destination, with autoFlush one that holds more than a block header
                return OperationStatus.DestinationTooSmall;
            }
            else
            {
                // A whole destination smaller than a block, while lz4 may hold nearly a block. The flush writes that block,
                // and lz4 keeps what does not fit for the next call. A larger destination never comes here, so its blocks stay full.
                status = FlushCore(context, destination, ref bytesWritten);
                if (status != OperationStatus.Done) return status;
            }
        }

        return isFinalBlock ? CloseCore(context, destination, ref bytesWritten) : OperationStatus.Done;
    }

    /// <summary>
    /// Writes out everything the encoder holds so far, without ending the frame.
    /// </summary>
    /// <param name="destination">The buffer to write flushed data to.</param>
    /// <param name="bytesWritten">When this method returns, contains the number of bytes written to destination.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> when everything is written;
    /// <see cref="OperationStatus.DestinationTooSmall"/> when more is left, call again with more room;
    /// <see cref="OperationStatus.InvalidData"/> on failure, bytesWritten is then 0. Call <see cref="Reset()"/> before reusing the encoder, or dispose it.
    /// </returns>
    /// <exception cref="ObjectDisposedException">Thrown when the encoder has been disposed.</exception>
    /// <remarks>
    /// This is useful when you need to ensure all input data has been processed and output, for example when streaming over a network.
    /// Nothing is written before the first input of a frame. A destination of <see cref="GetMaxFlushBufferLength"/> finishes in one call.
    /// While a Close is unfinished, Flush returns <see cref="OperationStatus.InvalidData"/> and the frame is lost, so finish the Close instead.
    /// </remarks>
    public OperationStatus Flush(Span<byte> destination, out int bytesWritten)
    {
        var context = GetContext();
        bytesWritten = 0;

        if (state == FrameState.Faulted) return OperationStatus.InvalidData;
        if (closing) return Fault(); // the frame is being closed, only Close can follow

        // nothing is buffered before a frame begins
        if (state == FrameState.Idle) return OperationStatus.Done;

        return FlushCore(context, destination, ref bytesWritten);
    }

    /// <summary>
    /// Ends the current frame and writes out everything the encoder holds. The encoder can then compress another frame.
    /// </summary>
    /// <param name="destination">The buffer to write the rest of the frame to.</param>
    /// <param name="bytesWritten">When this method returns, contains the number of bytes written to destination.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> when the frame is completely written;
    /// <see cref="OperationStatus.DestinationTooSmall"/> when more is left, call again with more room;
    /// <see cref="OperationStatus.InvalidData"/> on failure, for example the frame length differs from ContentSize, bytesWritten is then 0.
    /// Call <see cref="Reset()"/> before reusing the encoder, or dispose it.
    /// </returns>
    /// <exception cref="ObjectDisposedException">Thrown when the encoder has been disposed.</exception>
    /// <remarks>
    /// Closing an encoder that has not compressed anything writes an empty frame.
    /// A destination of <see cref="GetMaxFlushBufferLength"/> finishes in one call.
    /// </remarks>
    public OperationStatus Close(Span<byte> destination, out int bytesWritten)
    {
        var context = GetContext();
        bytesWritten = 0;
        var status = CloseCore(context, destination, ref bytesWritten);
        if (status == OperationStatus.InvalidData) bytesWritten = 0; // nothing of a broken frame counts, the same as ZstandardEncoder
        return status;
    }

    /// <summary>
    /// Abandons the current frame, if one is in progress, so the next <see cref="Compress"/> starts a new frame with the same options.
    /// Data buffered for the abandoned frame is discarded.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the encoder has been disposed.</exception>
    public void Reset()
    {
        ThrowIfDisposed();

        // the next call begins a frame, which drops whatever lz4 still holds of this one
        state = FrameState.Idle;
        closing = false;
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
            ValidateFrame(); // also stops the context from referencing the previous dictionary
        }
        catch
        {
            preferences = previousPreferences;
            dictionary = previousDictionary;
            next.Dispose();
            Reset(); // the next call begins a new frame, whether or not the failed begin dropped the one in progress
            throw;
        }

        previousDictionary.Dispose();
        Reset();
    }

    // Begins a frame with the current options and dictionary, so they are checked right away. The frame itself is not used,
    // the next call begins another one.
    void ValidateFrame()
    {
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
    }

    // Writes what has to come before new input, the header of a new frame or the rest of a flush that did not fit.
    OperationStatus Prepare(LZ4F_cctx_s* context, Span<byte> destination, ref int written)
    {
        if (state == FrameState.Faulted) return OperationStatus.InvalidData;

        if (state == FrameState.Idle)
        {
            var status = Begin(context, destination, ref written);
            if (status != OperationStatus.Done) return status;
        }

        return state == FrameState.Flushing ? FlushCore(context, destination, ref written) : OperationStatus.Done;
    }

    OperationStatus Begin(LZ4F_cctx_s* context, Span<byte> destination, ref int written)
    {
        // lz4 writes the header only into a destination that holds all of it
        var target = destination.Slice(written);
        if (target.Length < GetActualFrameHeaderLength()) return OperationStatus.DestinationTooSmall;

        nuint result;
        fixed (LZ4F_preferences_t* prefs = &preferences)
        fixed (byte* dest = target)
        {
            result = dictionary.IsEmpty
                ? LZ4F_compressBegin(context, dest, (nuint)target.Length, prefs)
                : LZ4F_compressBegin_usingCDict(context, dest, (nuint)target.Length, dictionary.Compression, prefs);
            GC.KeepAlive(this);
        }

        if (LZ4.IsError(result)) return OperationStatus.InvalidData; // no frame began, so the encoder stays idle

        state = FrameState.Open;
        buffered = 0;
        written += (int)result;
        return OperationStatus.Done;
    }

    bool Update(LZ4F_cctx_s* context, ReadOnlySpan<byte> source, Span<byte> destination, out int written)
    {
        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            // LZ4F_compressOptions_t(stableSrc) would ask lz4 to read the source again later, so null is passed.
            var result = LZ4F_compressUpdate(context, dest, (nuint)destination.Length, src, (nuint)source.Length, null);
            GC.KeepAlive(this);

            var failed = LZ4.IsError(result);
            written = failed ? 0 : (int)result;
            if (preferences.autoFlush == 0)
            {
                buffered = (int)((buffered + (long)source.Length) % BlockSize);
            }
            return !failed;
        }
    }

    // Called in Open or Flushing.
    OperationStatus FlushCore(LZ4F_cctx_s* context, Span<byte> destination, ref int written)
    {
        var room = destination.Slice(written);

        nuint result;
        nuint remains = 0;
        fixed (byte* dest = room)
        {
            result = room.Length == 0 ? 0 : LZ4F_flush(context, dest, (nuint)room.Length, null);

            // A full destination may leave output in lz4. Flushing into no room fails only while output remains, without changing the frame.
            if (!LZ4.IsError(result) && result == (nuint)room.Length)
            {
                remains = LZ4F_flush(context, null, 0, null);
            }
            GC.KeepAlive(this);
        }

        if (LZ4.IsError(result)) return Fault();
        written += (int)result;

        if (LZ4.IsError(remains))
        {
            state = FrameState.Flushing;
            return OperationStatus.DestinationTooSmall;
        }

        state = FrameState.Open;
        buffered = 0;
        return OperationStatus.Done;
    }

    OperationStatus CloseCore(LZ4F_cctx_s* context, Span<byte> destination, ref int written)
    {
        if (state == FrameState.Faulted) return OperationStatus.InvalidData;
        closing = true;

        if (state != FrameState.Ending)
        {
            // also begins an empty frame when nothing was compressed
            var status = Prepare(context, destination, ref written);
            if (status != OperationStatus.Done) return status;

            // Everything buffered goes out first, so what LZ4F_compressEnd writes is the frame ending alone.
            status = FlushCore(context, destination, ref written);
            if (status != OperationStatus.Done) return status;

            state = FrameState.Ending;
            footerLeft = GetActualFrameFooterLength();
        }

        var room = destination.Slice(written);
        if (room.Length == 0) return OperationStatus.DestinationTooSmall;

        nuint result;
        fixed (byte* dest = room)
        {
            result = LZ4F_compressEnd(context, dest, (nuint)room.Length, null);
            GC.KeepAlive(this);
        }

        if (LZ4.IsError(result)) return Fault(); // for example the frame length differs from ContentSize
        written += (int)result;
        footerLeft -= (int)result;
        if (footerLeft > 0) return OperationStatus.DestinationTooSmall;

        state = FrameState.Idle;
        closing = false;
        return OperationStatus.Done;
    }

    // How much input is sure to fit the room, assuming every block is stored uncompressed.
    int GetMaxSourceLength(int room)
    {
        long blockSize = BlockSize;
        long unit = blockSize + BlockOverhead;
        long blocks = room / unit;
        long length;
        if (preferences.autoFlush != 0)
        {
            // nothing is buffered with autoFlush, the rest of the input becomes one more, smaller block
            length = blocks * blockSize + Math.Max(0, room - blocks * unit - BlockOverhead);
        }
        else
        {
            // input completes a block only together with what lz4 already holds, the rest stays buffered
            length = (blocks + 1) * blockSize - 1 - buffered;
        }
        return (int)Math.Min(length, int.MaxValue);
    }

    // The constructor and Reset(options) let lz4 check the options, so the block size is a valid one.
    int BlockSize => LZ4.GetMaxBlockSize((BlockSizeId)preferences.frameInfo.blockSizeID);

    // block header, and the block checksum when enabled
    int BlockOverhead => preferences.frameInfo.blockChecksumFlag == LZ4F_blockChecksum_t.LZ4F_blockChecksumEnabled ? 8 : 4;

    OperationStatus Fault()
    {
        state = FrameState.Faulted;
        return OperationStatus.InvalidData;
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
