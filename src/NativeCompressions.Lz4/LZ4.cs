using NativeCompressions.LZ4.Raw;
using System.Runtime.CompilerServices;
using static NativeCompressions.LZ4.Raw.NativeMethods;

namespace NativeCompressions.LZ4;

public static partial class LZ4
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

    /// <summary>
    /// Gets the minimum number of bytes required to determine the LZ4 frame header size.
    /// </summary>
    /// <returns>The minimum bytes needed (5 bytes) to identify header size.</returns>
    /// <remarks>
    /// This is the smallest amount of data needed to parse the frame's magic number
    /// and flags to determine the full header size. Use this value to ensure you have
    /// enough data before calling <see cref="GetHeaderSize"/>.
    /// </remarks>
    public static int GetMinSizeToKnowHeaderLength() => 5; // LZ4F_MIN_SIZE_TO_KNOW_HEADER_LENGTH

    public static int GetMaxCompressedLength(int inputSize, in LZ4FrameOptions options)
    {
        ref var preferences_t = ref Unsafe.As<LZ4FrameOptions, LZ4F_preferences_t>(ref Unsafe.AsRef(in options));
        unsafe
        {
            // calculate bound for LZ4F_compressFrame
            // compressFrameBound uses max header size so changing ContentSize and DictionaryId is ok(in Compress methods, we changes it)
            var bound = LZ4F_compressFrameBound((nuint)inputSize, (LZ4F_preferences_t*)Unsafe.AsPointer(ref preferences_t));
            return (int)bound;
        }
    }

    public static bool TryGetFrameInfo(ReadOnlySpan<byte> source, out LZ4FrameInfo frameInfo)
    {
        using var decoder = new LZ4Decoder();
        
        if(source.Length < GetMinSizeToKnowHeaderLength())
        {
            frameInfo = default;
            return false;
        }

        var headerSize = decoder.GetHeaderSize(source);
        if (headerSize == 0 || source.Length < headerSize)
        {
            frameInfo = default;
            return false;
        }

        frameInfo = decoder.GetFrameInfo(source, out var _);
        return true;
    }

    internal static void HandleErrorCode(nuint code)
    {
        if (LZ4F_isError(code) != 0)
        {
            var error = GetErrorName(code);
            throw new LZ4Exception(error);
        }
    }

    static unsafe string GetErrorName(nuint code)
    {
        var name = (sbyte*)LZ4F_getErrorName(code);
        return new string(name);
    }
}
