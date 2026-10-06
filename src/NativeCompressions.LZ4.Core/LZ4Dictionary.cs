using NativeCompressions.Internal;
using NativeCompressions.Interop;
using System.Runtime.InteropServices;
using static NativeCompressions.Interop.LZ4NativeMethods;

namespace NativeCompressions;

/// <summary>
/// A prepared LZ4 dictionary usable for both compression and decompression.
/// </summary>
/// <remarks>
/// Create with <see cref="Create"/>. The dictionary can be shared by many encoders and decoders,
/// and must stay alive and undisposed while any of them uses it.
/// LZ4 frames only record the id you pass, so the reader must obtain the same dictionary by other means.
/// </remarks>
public sealed unsafe class LZ4Dictionary : IDisposable
{
    // A SafeHandle, because an encoder reads the native dictionary for every frame it starts until its context is freed.
    // Encoders and decoders hold a lease for that time, so neither Dispose nor the order of finalizers frees it earlier.
    // The pinned bytes for decompression are released together with the native dictionary.
    readonly NativeDictionary native;

    // The handle reports closed only after the last reference is gone, this is set by Dispose right away.
    int disposed;

    readonly byte[] data;

    LZ4Dictionary(byte[] data, NativeDictionary native, uint dictionaryId)
    {
        this.data = data;
        this.native = native;
        this.DictionaryId = dictionaryId;
    }

    /// <summary>
    /// Creates a dictionary from raw bytes. Any content works, but only the last 64KB are used.
    /// </summary>
    /// <remarks>
    /// LZ4 has no dictionary trainer of its own. Bytes trained from samples with <c>ZstandardDictionary.Train</c>
    /// are recommended, the last 64KB of representative data also works.
    /// </remarks>
    /// <param name="data">The dictionary bytes. A copy is kept in <see cref="Data"/>.</param>
    /// <param name="dictionaryId">An id written into frame headers so readers can pick the matching dictionary. 0 writes no id.</param>
    public static LZ4Dictionary Create(ReadOnlySpan<byte> data, uint dictionaryId = 0)
    {
        if (data.IsEmpty) throw new ArgumentException("Dictionary data cannot be empty.", nameof(data));

        var copy = data.ToArray();
        var pin = GCHandle.Alloc(copy, GCHandleType.Pinned); // .NET 10 has PinnedGCHandle<T> but we need to support .NET Standard 2.0 too.

        // The pin and the native dictionary are released here until the handle owns them, whatever fails.
        LZ4F_CDict_s* cdict = null;
        try
        {
            cdict = LZ4F_createCDict((byte*)pin.AddrOfPinnedObject(), (nuint)copy.Length);
            if (cdict == null) throw new LZ4Exception("Failed to create compression dictionary");

            var native = new NativeDictionary(cdict, pin);
            cdict = null;
            pin = default;
            return new LZ4Dictionary(copy, native, dictionaryId);
        }
        finally
        {
            if (cdict != null) LZ4F_freeCDict(cdict);
            if (pin.IsAllocated) pin.Free();
        }
    }

    /// <summary>
    /// Gets the dictionary bytes this instance was created from.
    /// </summary>
    public ReadOnlyMemory<byte> Data => data;

    /// <summary>
    /// Gets the id written into frame headers that use this dictionary. 0 means no id is written.
    /// </summary>
    public uint DictionaryId { get; }

    /// <summary>
    /// Gets a value indicating whether the dictionary has been disposed.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;


    internal Lease Acquire()
    {
        if (IsDisposed) Throws.ObjectDisposedException(nameof(LZ4Dictionary));

        var added = false;
        native.DangerousAddRef(ref added);
        return new Lease(this);
    }

    void Release() => native.DangerousRelease();

    /// <summary>
    /// Releases the native dictionary. Safe to call multiple times.
    /// </summary>
    /// <remarks>
    /// An encoder or decoder that still uses the dictionary keeps the native memory until it is done with it.
    /// </remarks>
    public void Dispose()
    {
        Volatile.Write(ref disposed, 1);
        native.Dispose();
    }

    // A lease keeps the native dictionary alive until it is disposed, also when the dictionary is disposed or
    // finalized meanwhile. It is the only way to the native pointers. A default lease stands for no dictionary.
    internal readonly struct Lease : IDisposable
    {
        readonly LZ4Dictionary? dictionary;

        public readonly LZ4F_CDict_s* Compression;
        public readonly byte* Data;
        public readonly int DataLength;

        internal Lease(LZ4Dictionary dictionary)
        {
            this.dictionary = dictionary;
            Compression = dictionary.native.Compression;
            Data = dictionary.native.Data;
            DataLength = dictionary.data.Length;
        }

        // true for the default lease, which stands for no dictionary
        public bool IsEmpty => dictionary == null;

        // The holder disposes a lease once, and does not use the pointers afterwards.
        public void Dispose() => dictionary?.Release();
    }

    sealed class NativeDictionary : SafeHandle
    {
        // Decompression hands the raw bytes to lz4frame, which keeps that address for the whole frame.
        // The array is pinned for the lifetime of this handle so the address never changes.
        GCHandle pin;

        public NativeDictionary(LZ4F_CDict_s* cdict, GCHandle pin)
            : base(IntPtr.Zero, ownsHandle: true)
        {
            this.pin = pin;
            SetHandle((IntPtr)cdict);
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        public LZ4F_CDict_s* Compression => (LZ4F_CDict_s*)handle;
        public byte* Data => (byte*)pin.AddrOfPinnedObject();

        protected override bool ReleaseHandle()
        {
            LZ4F_freeCDict((LZ4F_CDict_s*)handle);
            pin.Free();
            return true;
        }
    }
}
