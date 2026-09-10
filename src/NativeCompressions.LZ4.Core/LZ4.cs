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

    /// <summary>
    /// Gets the maximum compressed size of a whole frame for the given input size, including header and footer.
    /// </summary>
    public static int GetMaxCompressedLength(int inputSize) => GetMaxCompressedLength(inputSize, LZ4CompressionOptions.Default);

    /// <summary>
    /// Gets the maximum compressed size of a whole frame for the given input size and options, including header and footer.
    /// </summary>
    public static int GetMaxCompressedLength(int inputSize, in LZ4CompressionOptions options)
    {
        if (inputSize < 0) throw new ArgumentOutOfRangeException(nameof(inputSize));
        return checked((int)GetMaxCompressedLength((nuint)inputSize, options));
    }

    /// <summary>
    /// Gets the maximum compressed size of a whole frame for the given input size, including header and footer.
    /// </summary>
    public static nuint GetMaxCompressedLength(nuint inputSize) => GetMaxCompressedLength(inputSize, LZ4CompressionOptions.Default);

    /// <summary>
    /// Gets the maximum compressed size of a whole frame for the given input size and options, including header and footer.
    /// </summary>
    public static unsafe nuint GetMaxCompressedLength(nuint inputSize, in LZ4CompressionOptions options)
    {
        // compressFrameBound assumes the largest header, so ContentSize and DictionaryID do not matter here
        var preferences = options.ToPreferences();
        return LZ4F_compressFrameBound(inputSize, &preferences);
    }

    /// <summary>
    /// Reads the frame header at the start of source. Returns false when source is too short or does not start with an LZ4 frame.
    /// </summary>
    public static unsafe bool TryGetFrameInfo(ReadOnlySpan<byte> source, out LZ4FrameInfo frameInfo)
    {
        frameInfo = default;
        if (source.Length < MinSizeToKnowFrameHeaderLength)
        {
            return false;
        }

        fixed (byte* src = source)
        {
            var headerSizeOrError = LZ4F_headerSize(src, (nuint)source.Length);
            if (IsError(headerSizeOrError) || headerSizeOrError == 0 || (nuint)source.Length < headerSizeOrError)
            {
                return false;
            }
        }

        LZ4F_dctx_s* context = null;
        var code = LZ4F_createDecompressionContext(&context, FrameVersion);
        ThrowIfError(code);
        try
        {
            fixed (byte* src = source)
            {
                ref var native = ref Unsafe.As<LZ4FrameInfo, LZ4F_frameInfo_t>(ref frameInfo);
                var consumed = (nuint)source.Length;
                var result = LZ4F_getFrameInfo(context, (LZ4F_frameInfo_t*)Unsafe.AsPointer(ref native), src, &consumed);
                if (IsError(result))
                {
                    frameInfo = default;
                    return false;
                }
                return true;
            }
        }
        finally
        {
            LZ4F_freeDecompressionContext(context);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsError(nuint code)
    {
        return LZ4F_isError(code) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ThrowIfError(nuint code)
    {
        if (LZ4F_isError(code) != 0)
        {
            Throw(code);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Throw(nuint code)
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
