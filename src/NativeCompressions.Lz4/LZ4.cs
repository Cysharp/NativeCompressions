using NativeCompressions.LZ4.Raw;
using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static NativeCompressions.LZ4.Raw.NativeMethods;

namespace NativeCompressions.LZ4;

public static class LZ4
{
    static string? version;

    /// <summary>
    /// Gets the version string of the LZ4 library.
    /// </summary>
    public static string Version
    {
        get
        {
            if (version == null)
            {
                unsafe
                {
                    // null-terminated
                    version = new string((sbyte*)LZ4_versionString());
                }
            }
            return version;
        }
    }

    /// <summary>
    /// Gets the version number of the LZ4 library.
    /// </summary>
    public static int VersionNumber => LZ4_versionNumber();

    /// <summary>
    /// Gets the version of the LZ4 frame format supported by the library.
    /// </summary>
    public static uint FrameVersion => LZ4F_getVersion();

    /// <summary>
    /// Get the maximum allowed compression level.
    /// </summary>
    public static int GetMaxCompressionLevel()
    {
        return LZ4F_compressionLevel_max();
    }

    // TODO: rename
    public static int GetMaxCompressedLengthInFrame(int inputSize, in LZ4FrameOptions options)
    {
        // TODO: same as brotli validation
        // ArgumentOutOfRangeException.ThrowIfNegative(inputSize);
        // ArgumentOutOfRangeException.ThrowIfGreaterThan(inputSize, BrotliUtils.MaxInputSize);

        ref var preferences_t = ref Unsafe.As<LZ4FrameOptions, LZ4F_preferences_t>(ref Unsafe.AsRef(in options));
        unsafe
        {


            var bound = LZ4F_compressBound((nuint)inputSize, (LZ4F_preferences_t*)Unsafe.AsPointer(ref preferences_t));
            return (int)bound;
        }
    }

    public static int GetMaxCompressedLength(int inputSize, in LZ4FrameOptions options)
    {
        // TODO: same as brotli validation
        // ArgumentOutOfRangeException.ThrowIfNegative(inputSize);
        // ArgumentOutOfRangeException.ThrowIfGreaterThan(inputSize, BrotliUtils.MaxInputSize);

        ref var preferences_t = ref Unsafe.As<LZ4FrameOptions, LZ4F_preferences_t>(ref Unsafe.AsRef(in options));
        unsafe
        {
            var bound = LZ4F_compressFrameBound((nuint)inputSize, (LZ4F_preferences_t*)Unsafe.AsPointer(ref preferences_t));
            return (int)bound;
        }
    }

    public static unsafe byte[] Compress(ReadOnlySpan<byte> source) // TODO: Options, Dictionary.
    {
        var options = LZ4FrameOptions.Default.ToPreferences();
        var maxLength = LZ4F_compressFrameBound((uint)source.Length, options);

        var buffer = ArrayPool<byte>.Shared.Rent((int)maxLength);
        try
        {
            fixed (byte* src = source)
            fixed (byte* dest = buffer)
            {
                var bytesWrittenOrErrorCode = LZ4F_compressFrame(dest, (nuint)buffer.Length, src, (nuint)source.Length, options);
                HandleErrorCode(bytesWrittenOrErrorCode);
                return buffer.AsSpan(0, (int)bytesWrittenOrErrorCode).ToArray();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }


    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        // use one-shot operations

        // LZ4F_compressFrameBound
        // LZ4F_compressFrame
        // LZ4F_compressFrame_usingCDict(




        throw new NotSupportedException();
    }

    internal static void HandleErrorCode(nuint code)
    {
        if (LZ4F_isError(code) != 0)
        {
            var error = GetErrorName(code);
            throw new InvalidOperationException(error);
        }
    }

    static unsafe string GetErrorName(nuint code)
    {
        var name = (sbyte*)LZ4F_getErrorName(code);
        return new string(name);
    }
}

public sealed class LZ4CompressionDictionary : SafeHandle
{
    readonly byte[] dictionaryData;

    public override bool IsInvalid => handle == IntPtr.Zero;

    // for decompression
    internal ReadOnlySpan<byte> RawDictionary => dictionaryData;

    internal unsafe LZ4F_CDict_s* CompressionDictionary => ((LZ4F_CDict_s*)handle);

    public LZ4CompressionDictionary(ReadOnlySpan<byte> dictionaryData)
        : base(IntPtr.Zero, true)
    {
        if (dictionaryData.Length == 0) throw new ArgumentException("Dictionary data cannot be empty", nameof(dictionaryData));

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