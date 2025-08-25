using NativeCompressions.LZ4.Raw;
using System.Runtime.InteropServices;
using static NativeCompressions.LZ4.Raw.NativeMethods;

namespace NativeCompressions.LZ4;

public sealed class LZ4CompressionDictionary : SafeHandle
{
    readonly byte[] dictionaryData;

    public override bool IsInvalid => handle == IntPtr.Zero;

    public uint DictionaryId { get; private set; }

    // for decompression
    internal ReadOnlySpan<byte> RawDictionary => dictionaryData;

    internal unsafe LZ4F_CDict_s* Handle => ((LZ4F_CDict_s*)handle);

    public LZ4CompressionDictionary(ReadOnlySpan<byte> dictionaryData, uint dictionaryId)
        : base(IntPtr.Zero, true)
    {
        if (dictionaryData.Length == 0) throw new ArgumentException("Dictionary data cannot be empty", nameof(dictionaryData));

        this.DictionaryId = dictionaryId;

        var data = dictionaryData.ToArray(); // diffencive copy
        unsafe
        {
            fixed (void* p = data)
            {
                var handle = LZ4F_createCDict(p, (UIntPtr)data.Length);
                SetHandle((IntPtr)handle);
                this.dictionaryData = data;
            }
        }
    }

    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero)
        {
            unsafe
            {
                LZ4F_freeCDict((LZ4F_CDict_s*)handle);
            }
            handle = IntPtr.Zero;
            return true;
        }
        return false;
    }
}