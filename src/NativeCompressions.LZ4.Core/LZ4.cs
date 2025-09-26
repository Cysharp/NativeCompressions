using NativeCompressions.Interop;
using System.Runtime.CompilerServices;
using static NativeCompressions.Interop.LZ4NativeMethods;

namespace NativeCompressions;

public static partial class LZ4
{
    static unsafe LZ4()
    {
        Version = new string((sbyte*)LZ4_versionString());
        VersionNumber = LZ4_versionNumber();
        FrameVersion = LZ4F_getVersion();
        MaxCompressionLevel = LZ4F_compressionLevel_max();
    }

    /// <summary>
    /// Gets the version string of the LZ4 library.
    /// </summary>
    public static readonly string Version;

    /// <summary>
    /// Gets the version number of the LZ4 library.
    /// </summary>
    public static readonly int VersionNumber;

    /// <summary>
    /// Gets the version of the LZ4 frame format supported by the library.
    /// </summary>
    public static readonly uint FrameVersion;

    /// <summary>
    /// Get the minimum compression level.
    /// </summary>
    public const int MinCompressionLevel = 1;

    /// <summary>
    /// Get the maximum compression level.
    /// </summary>
    public static readonly int MaxCompressionLevel;

    /// <summary>
    /// Gets the default compression level.
    /// </summary>
    public const int DefaultCompressionLevel = 1;

    /// <summary>
    /// Gets the minimum number of bytes required to determine the LZ4 frame header size.
    /// </summary>
    /// <returns>The minimum bytes needed (5 bytes) to identify header size.</returns>
    /// <remarks>
    /// This is the smallest amount of data needed to parse the frame's magic number
    /// and flags to determine the full header size. Use this value to ensure you have
    /// enough data before calling <see cref="GetHeaderSize"/>.
    /// </remarks>
    public const int MinSizeToKnowFrameHeaderLength = 5; // LZ4F_MIN_SIZE_TO_KNOW_HEADER_LENGTH

    /// <summary>
    /// Gets the maximum possible size of an LZ4 frame header.
    /// </summary>
    /// <returns>Maximum header size in bytes (19 bytes).</returns>
    /// <remarks>
    /// The actual header size depends on enabled options:
    /// - Base header: 7 bytes (magic number, flags, block size)
    /// - Content size field: +8 bytes (if enabled)
    /// - Dictionary ID: +4 bytes (if present)
    /// - Header checksum: +1 byte
    /// </remarks>
    public const int MaxFrameHeaderLength = 19; // LZ4F_HEADER_SIZE_MAX

    /// <summary>
    /// Gets the maximum possible size of an LZ4 frame footer.
    /// </summary>
    /// <returns>Maximum footer size in bytes (8 bytes).</returns>
    /// <remarks>
    /// The footer consists of:
    /// - End mark: 4 bytes (always present)
    /// - Content checksum: 4 bytes (if content checksum is enabled)
    /// </remarks>
    public const int MaxFrameFooterLength = 8;  // EndMarkSize + ChecksumSize

    public static int GetMaxCompressedLength(int inputSize) => GetMaxCompressedLength(inputSize, LZ4FrameOptions.Default);

    public static int GetMaxCompressedLength(int inputSize, in LZ4FrameOptions options) => checked((int)GetMaxCompressedLongLength(inputSize, options));

    public static long GetMaxCompressedLongLength(int inputSize) => GetMaxCompressedLongLength(inputSize, LZ4FrameOptions.Default);

    public static long GetMaxCompressedLongLength(int inputSize, in LZ4FrameOptions options)
    {
        ref var preferences_t = ref Unsafe.As<LZ4FrameOptions, LZ4F_preferences_t>(ref Unsafe.AsRef(in options));
        unsafe
        {
            // calculate bound for LZ4F_compressFrame
            // compressFrameBound uses max header size so changing ContentSize and DictionaryId is ok(in Compress methods, we changes it)
            var bound = LZ4F_compressFrameBound((nuint)inputSize, (LZ4F_preferences_t*)Unsafe.AsPointer(ref preferences_t));
            return (long)bound;
        }
    }

    public static bool TryGetFrameInfo(ReadOnlySpan<byte> source, out LZ4FrameInfo frameInfo)
    {
        using var decoder = new LZ4Decoder();

        if (source.Length < MinSizeToKnowFrameHeaderLength)
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

    internal static bool IsError(nuint code)
    {
        return LZ4F_isError(code) != 0;
    }

    internal static void ThrowIfError(nuint code)
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
