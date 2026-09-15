using NativeCompressions.Internal;
using NativeCompressions.Interop;
using System.Runtime.CompilerServices;
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
    // Held as a raw pointer instead of SafeHandle to keep a single managed allocation.
    // Released by Dispose or the finalizer.
    LZ4F_CDict_s* cdict;

    readonly byte[] data;

    LZ4Dictionary(byte[] data, uint dictionaryId, LZ4F_CDict_s* cdict)
    {
        this.data = data;
        this.DictionaryId = dictionaryId;
        this.cdict = cdict;
    }

    ~LZ4Dictionary()
    {
        var handle = cdict;
        if (handle != null)
        {
            cdict = null;
            LZ4F_freeCDict(handle);
        }
    }

    /// <summary>
    /// Creates a dictionary from raw bytes. Any content works, typically the last 64KB of representative data.
    /// </summary>
    /// <param name="data">The dictionary bytes. A copy is kept in <see cref="Data"/>.</param>
    /// <param name="dictionaryId">An id written into frame headers so readers can pick the matching dictionary. 0 writes no id.</param>
    public static LZ4Dictionary Create(ReadOnlySpan<byte> data, uint dictionaryId = 0)
    {
        if (data.IsEmpty) throw new ArgumentException("Dictionary data cannot be empty.", nameof(data));

        var copy = data.ToArray();
        fixed (byte* p = copy)
        {
            var cdict = LZ4F_createCDict(p, (nuint)copy.Length);
            if (cdict == null) throw new LZ4Exception("Failed to create compression dictionary");

            return new LZ4Dictionary(copy, dictionaryId, cdict);
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
    public bool IsDisposed => cdict == null;

    // for decompression, LZ4F reads the raw bytes directly
    internal ReadOnlySpan<byte> RawDictionary
    {
        get
        {
            ThrowIfDisposed();
            return data;
        }
    }

    internal LZ4F_CDict_s* Handle
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var handle = cdict;
            if (handle == null) Throws.ObjectDisposedException(nameof(LZ4Dictionary));
            return handle;
        }
    }

    /// <summary>
    /// Releases the native dictionary. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        // Interlocked has no pointer overload, so swap the field as an IntPtr while it is pinned.
        LZ4F_CDict_s* handle;
        fixed (LZ4F_CDict_s** p = &cdict)
        {
            handle = (LZ4F_CDict_s*)Interlocked.Exchange(ref *(IntPtr*)p, IntPtr.Zero);
        }

        if (handle != null)
        {
            LZ4F_freeCDict(handle);
        }
        GC.SuppressFinalize(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void ThrowIfDisposed()
    {
        if (cdict == null) Throws.ObjectDisposedException(nameof(LZ4Dictionary));
    }
}
