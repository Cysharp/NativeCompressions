using System.Buffers;
using size_t = System.UIntPtr; // nuint is .NET size_t equivalent, internally nuint is represent as UIntPtr.

namespace NativeCompressions.ZStandard;

public enum FrameContentSizeResult
{
    Succeed,
    Empty,
    Unknown,
    Error
}

/// <summary>
/// Managed libzstd standard operations.
/// </summary>
public static unsafe class ZStandard
{
    static int? defaultCompressionLevel;

    /// <summary>
    /// Get the default compression level.
    /// </summary>
    public static int DefaultCompressionLevel
    {
        get
        {
            int? level = defaultCompressionLevel;
            if (level == null)
            {
                level = defaultCompressionLevel = LibZstd.ZSTD_defaultCLevel();
            }
            return level.Value;
        }
    }
    /// <summary>
    /// Get the libzstd dll version.
    /// </summary>
    public static string Version
    {
        get
        {
            return new string(LibZstd.ZSTD_versionString());
        }
    }

    /// <summary>
    /// Get the readable error name from function result size_t.
    /// </summary>
    public static string GetErrorName(size_t code)
    {
        return new string(LibZstd.ZSTD_getErrorName(code));
    }

    /// <summary>
    /// Get the minimum negative compression level allowed.
    /// </summary>
    public static int MinimumCompressionLevel => LibZstd.ZSTD_minCLevel();

    /// <summary>
    /// Get the maximum compression level available.
    /// </summary>
    public static int MaximumCompressionLevel => LibZstd.ZSTD_maxCLevel();

    // TODO:more api

    /// <summary>
    /// Compress the input value.
    /// </summary>
    public static byte[] Compress(byte[] src, int? compressionLevel = default)
    {
        var level = compressionLevel ?? DefaultCompressionLevel;
        var dest = ArrayPool<byte>.Shared.Rent((int)LibZstd.ZSTD_compressBound((size_t)src.Length));
        try
        {
            fixed (byte* srcP = src)
            fixed (byte* destP = dest)
            {
                var compressedSize = LibZstd.ZSTD_compress(destP, (size_t)dest.Length, srcP, (size_t)src.Length, level);

                if ((uint)compressedSize > dest.Length)
                {
                    throw new InvalidOperationException("Failed ZSTD_compress, dstCapacity is invalid.");
                }

                var isError = LibZstd.ZSTD_isError(compressedSize);
                if (isError != 0)
                {
                    throw new InvalidOperationException("Failed ZSTD_compress, " + GetErrorName(compressedSize));
                }

                return dest.AsSpan(0, (int)compressedSize).ToArray();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(dest);
        }
    }

    // TODO:more api

    /// <summary>
    /// Decompress the input value.
    /// </summary>
    public static byte[] Decompress(byte[] src)
    {
        var sizeResult = TryGetFrameContentSize(src, out var size);
        if (sizeResult == FrameContentSizeResult.Empty) return Array.Empty<byte>();
        if (sizeResult == FrameContentSizeResult.Error || sizeResult == FrameContentSizeResult.Unknown)
        {
            throw new InvalidOperationException("Binary frame is invalid. FrameContentSizeResult:" + sizeResult);
        }
        if (size > (ulong)Array.MaxLength)
        {
            throw new InvalidOperationException("Decompressed size is larger than Array.MaxLength. Size:" + size);
        }

        var dest = new byte[size];
        fixed (byte* srcP = src)
        fixed (byte* destP = dest)
        {
            var decompressedSize = LibZstd.ZSTD_decompress(destP, (size_t)size, srcP, (size_t)src.Length);

            if ((uint)decompressedSize > dest.Length)
            {
                throw new InvalidOperationException("Failed ZSTD_decompress, dstCapacity is invalid.");
            }

            var isError = LibZstd.ZSTD_isError(decompressedSize);
            if (isError != 0)
            {
                throw new InvalidOperationException("Failed ZSTD_decompress, " + GetErrorName(decompressedSize));
            }

            if (dest.Length == (int)decompressedSize)
            {
                return dest;
            }
            else
            {
                return dest.AsSpan(0, (int)decompressedSize).ToArray();
            }
        }
    }

    public static FrameContentSizeResult TryGetFrameContentSize(ReadOnlySpan<byte> src, out ulong size)
    {
        fixed (byte* p = src)
        {
            size = LibZstd.ZSTD_getFrameContentSize(p, (size_t)src.Length);
            if (size == LibZstd.ZSTD_CONTENTSIZE_UNKNOWN)
            {
                return FrameContentSizeResult.Unknown;
            }
            else if (size == LibZstd.ZSTD_CONTENTSIZE_ERROR)
            {
                return FrameContentSizeResult.Error;
            }
            else if (size == 0)
            {
                return FrameContentSizeResult.Empty;
            }

            return FrameContentSizeResult.Succeed;
        }
    }
}