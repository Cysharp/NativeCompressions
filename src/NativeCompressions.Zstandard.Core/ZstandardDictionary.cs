using NativeCompressions.Internal;
using NativeCompressions.Interop;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static NativeCompressions.Interop.ZstandardNativeMethods;

namespace NativeCompressions;

/// <summary>
/// A prepared Zstandard dictionary usable for both compression and decompression.
/// </summary>
/// <remarks>
/// Create with <see cref="Create"/>. The dictionary can be shared by many encoders and decoders,
/// and must stay alive and undisposed while any of them uses it.
/// The compression side is prepared for a single compression level. Create another dictionary for a different level.
/// </remarks>
public sealed unsafe class ZstandardDictionary : IDisposable
{
    // A SafeHandle, because an encoder with worker threads reads the native dictionary until its context is freed.
    // The encoder takes a reference for that time, so neither Dispose nor the order of finalizers frees it earlier.
    readonly NativeDictionaries native;

    // The handle reports closed only after the last reference is gone, this is set by Dispose right away.
    int disposed;

    readonly byte[] data;

    ZstandardDictionary(byte[] data, int compressionLevel, NativeDictionaries native, uint dictionaryId)
    {
        this.data = data;
        this.CompressionLevel = compressionLevel;
        this.native = native;
        this.DictionaryId = dictionaryId;
    }

    sealed class NativeDictionaries : SafeHandle
    {
        readonly ZSTD_DDict_s* ddict;

        public NativeDictionaries(ZSTD_CDict_s* cdict, ZSTD_DDict_s* ddict)
            : base(IntPtr.Zero, ownsHandle: true)
        {
            this.ddict = ddict;
            SetHandle((IntPtr)cdict);
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        public ZSTD_CDict_s* Compression => (ZSTD_CDict_s*)handle;
        public ZSTD_DDict_s* Decompression => ddict;

        protected override bool ReleaseHandle()
        {
            ZSTD_freeCDict((ZSTD_CDict_s*)handle);
            ZSTD_freeDDict(ddict);
            return true;
        }
    }

    /// <summary>
    /// Creates a dictionary from raw dictionary bytes, either a trained zstd dictionary or arbitrary content.
    /// </summary>
    /// <param name="data">The dictionary bytes. A copy is kept in <see cref="Data"/>.</param>
    /// <param name="compressionLevel">The compression level the compression side is prepared for.</param>
    public static ZstandardDictionary Create(ReadOnlySpan<byte> data, int compressionLevel = Zstandard.DefaultCompressionLevel)
    {
        if (data.IsEmpty) throw new ArgumentException("Dictionary data cannot be empty.", nameof(data));

        var copy = data.ToArray();
        fixed (byte* p = copy)
        {
            var cdict = ZSTD_createCDict(p, (nuint)copy.Length, compressionLevel);
            if (cdict == null) throw new ZstandardException("Failed to create compression dictionary");

            var ddict = ZSTD_createDDict(p, (nuint)copy.Length);
            if (ddict == null)
            {
                ZSTD_freeCDict(cdict);
                throw new ZstandardException("Failed to create decompression dictionary");
            }

            var id = ZSTD_getDictID_fromDict(p, (nuint)copy.Length);
            return new ZstandardDictionary(copy, compressionLevel, new NativeDictionaries(cdict, ddict), id);
        }
    }

    /// <summary>
    /// Trains a dictionary from samples and prepares it for compression and decompression.
    /// </summary>
    /// <param name="samples">All samples concatenated into one buffer.</param>
    /// <param name="sampleLengths">The length of each sample in <paramref name="samples"/>, in order.</param>
    /// <param name="maxDictionarySize">The upper bound of the trained dictionary size. About 100 KB is a typical choice.</param>
    /// <param name="compressionLevel">The compression level the compression side is prepared for.</param>
    /// <remarks>
    /// Training fails when there are too few samples or most samples are shorter than 8 bytes.
    /// zstd recommends a few thousand samples whose total size is roughly 100 times the dictionary size.
    /// </remarks>
    public static ZstandardDictionary Train(ReadOnlySpan<byte> samples, ReadOnlySpan<int> sampleLengths, int maxDictionarySize, int compressionLevel = Zstandard.DefaultCompressionLevel)
    {
        var trained = TrainCore(samples, sampleLengths, maxDictionarySize);
        return Create(trained, compressionLevel);
    }

    static byte[] TrainCore(ReadOnlySpan<byte> samples, ReadOnlySpan<int> sampleLengths, int maxDictionarySize)
    {
        if (maxDictionarySize <= 0) throw new ArgumentOutOfRangeException(nameof(maxDictionarySize));
        if (sampleLengths.IsEmpty) throw new ArgumentException("At least one sample is required.", nameof(sampleLengths));

        long total = 0;
        foreach (var length in sampleLengths)
        {
            if (length < 0) throw new ArgumentException("Sample lengths must not be negative.", nameof(sampleLengths));
            total += length;
        }
        if (total != samples.Length) throw new ArgumentException("The sum of sample lengths must equal the length of samples.", nameof(sampleLengths));

        var sizes = new nuint[sampleLengths.Length];
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = (nuint)sampleLengths[i];
        }

        var buffer = new byte[maxDictionarySize];
        nuint result;
        fixed (byte* dict = buffer)
        fixed (byte* src = samples)
        fixed (nuint* sizesPtr = sizes)
        {
            result = ZDICT_trainFromBuffer(dict, (nuint)buffer.Length, src, sizesPtr, (uint)sizes.Length);
        }

        if (ZDICT_isError(result) != 0)
        {
            throw new ZstandardException(new string((sbyte*)ZDICT_getErrorName(result)));
        }

        var size = (int)result;
        return size == buffer.Length ? buffer : buffer.AsSpan(0, size).ToArray();
    }

    /// <summary>
    /// Gets the dictionary bytes this instance was created from.
    /// </summary>
    public ReadOnlyMemory<byte> Data => data;

    /// <summary>
    /// Gets the dictionary id stored in the dictionary header, or 0 for raw content dictionaries.
    /// </summary>
    public uint DictionaryId { get; }

    /// <summary>
    /// Gets the compression level the compression side was prepared for.
    /// </summary>
    public int CompressionLevel { get; }

    /// <summary>
    /// Gets a value indicating whether the dictionary has been disposed.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    // The caller keeps this instance reachable for as long as it uses the pointer.
    internal ZSTD_CDict_s* CompressionHandle
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (IsDisposed) Throws.ObjectDisposedException(nameof(ZstandardDictionary));
            return native.Compression;
        }
    }

    internal ZSTD_DDict_s* DecompressionHandle
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (IsDisposed) Throws.ObjectDisposedException(nameof(ZstandardDictionary));
            return native.Decompression;
        }
    }

    // Keeps the native dictionaries alive until ReleaseReference, also when the dictionary is disposed or finalized meanwhile.
    internal void AddReference()
    {
        if (IsDisposed) Throws.ObjectDisposedException(nameof(ZstandardDictionary));

        var added = false;
        native.DangerousAddRef(ref added);
    }

    internal void ReleaseReference()
    {
        native.DangerousRelease();
    }

    /// <summary>
    /// Releases the native dictionaries. Safe to call multiple times.
    /// </summary>
    /// <remarks>
    /// An encoder that still uses the dictionary keeps the native memory until it is done with it.
    /// </remarks>
    public void Dispose()
    {
        Volatile.Write(ref disposed, 1);
        native.Dispose();
    }
}
