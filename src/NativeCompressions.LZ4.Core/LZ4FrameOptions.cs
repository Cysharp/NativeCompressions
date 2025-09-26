using NativeCompressions.Interop;
using System.Runtime.InteropServices;

namespace NativeCompressions;

// LZ4FrameOptions is NativeMethods.LZ4F_preferences_t + NativeMethods.LZ4F_frameInfo_t + hold Dictionary

[StructLayout(LayoutKind.Auto)]
public readonly record struct LZ4FrameOptions
{
    public static readonly LZ4FrameOptions Default = new LZ4FrameOptions();

    readonly int compressionLevel;
    readonly uint autoFlush;
    readonly uint favorDecompressionSpeed;

    // flatten FrameInfo

    readonly BlockSizeId blockSizeID;
    readonly BlockMode blockMode;
    readonly ContentChecksum contentChecksumFlag;
    readonly FrameType frameType;
    readonly ulong contentSize;
    readonly uint dictionaryID;
    readonly BlockChecksum blockChecksumFlag;

    // other reference
    readonly LZ4CompressionDictionary? compressionDictionary;

    /// <summary>0: default (fast mode); values > LZ4HC_CLEVEL_MAX count as LZ4HC_CLEVEL_MAX; values < 0 trigger "fast acceleration"</summary>
    public int CompressionLevel
    {
        get => compressionLevel;
        init => compressionLevel = value;
    }

    /// <summary>true: always flush; reduces usage of internal buffers</summary>
    public bool AutoFlush
    {
        get => autoFlush == 1;
        init
        {
            if (value)
            {
                autoFlush = 1;
            }
            else
            {
                autoFlush = 0;
            }
        }
    }

    /// <summary>parser favors decompression speed vs compression ratio. Only works for high compression modes (>= LZ4HC_CLEVEL_OPT_MIN)</summary>
    public uint FavorDecompressionSpeed
    {
        get => favorDecompressionSpeed;
        init => favorDecompressionSpeed = value;
    }

    /// <summary>max64KB, max256KB, max1MB, max4MB; 0 == default (LZ4F_max64KB)</summary>
    public BlockSizeId BlockSizeID { get => blockSizeID; init => blockSizeID = value; }

    /// <summary>LZ4F_blockLinked, LZ4F_blockIndependent; 0 == default (LZ4F_blockLinked)</summary>
    public BlockMode BlockMode { get => blockMode; init => blockMode = value; }

    /// <summary>1: add a 32-bit checksum of frame's decompressed data; 0 == default (disabled)</summary>
    public ContentChecksum ContentChecksumFlag { get => contentChecksumFlag; init => contentChecksumFlag = value; }

    /// <summary>LZ4F_frame or LZ4F_skippableFrame</summary>
    public FrameType FrameType { get => frameType; init => frameType = value; }

    /// <summary>Size of uncompressed content ; 0 == unknown</summary>
    public ulong ContentSize { get => contentSize; init => contentSize = value; }

    /// <summary>Dictionary ID, sent by compressor to help decoder select correct dictionary; 0 == no dictID provided</summary>
    public uint DictionaryID { get => dictionaryID; init => dictionaryID = value; }

    /// <summary>1: each block followed by a checksum of block's compressed data; 0 == default (disabled)</summary>
    public BlockChecksum BlockChecksumFlag { get => blockChecksumFlag; init => blockChecksumFlag = value; }

    public LZ4CompressionDictionary? Dictionary
    {
        get
        {
            return compressionDictionary;
        }
        init
        {
            dictionaryID = (value == null)
                ? 0
                : value.DictionaryId;
            compressionDictionary = value;
        }
    }

    internal unsafe LZ4F_preferences_t ToPreferences()
    {
        var prefs = new LZ4F_preferences_t
        {
            autoFlush = autoFlush,
            compressionLevel = compressionLevel,
            favorDecSpeed = favorDecompressionSpeed,
            frameInfo = new LZ4F_frameInfo_t
            {
                blockSizeID = (int)blockSizeID,
                blockMode = (int)blockMode,
                contentChecksumFlag = (int)contentChecksumFlag,
                frameType = (int)frameType,
                contentSize = contentSize,
                dictID = dictionaryID,
                blockChecksumFlag = (int)blockChecksumFlag,
            }
        };

        return prefs;
    }
}

// same as NativeMethods.LZ4F_frameInfo_t
[StructLayout(LayoutKind.Sequential)]
public readonly record struct LZ4FrameInfo
{
    readonly BlockSizeId blockSizeID;
    readonly BlockMode blockMode;
    readonly ContentChecksum contentChecksumFlag;
    readonly FrameType frameType;
    readonly ulong contentSize;
    readonly uint dictionaryID;
    readonly BlockChecksum blockChecksumFlag;

    /// <summary>max64KB, max256KB, max1MB, max4MB; 0 == default (LZ4F_max64KB)</summary>
    public BlockSizeId BlockSizeID { get => blockSizeID; init => blockSizeID = value; }
    /// <summary>LZ4F_blockLinked, LZ4F_blockIndependent; 0 == default (LZ4F_blockLinked)</summary>
    public BlockMode BlockMode { get => blockMode; init => blockMode = value; }
    /// <summary>1: add a 32-bit checksum of frame's decompressed data; 0 == default (disabled)</summary>
    public ContentChecksum ContentChecksumFlag { get => contentChecksumFlag; init => contentChecksumFlag = value; }
    /// <summary>LZ4F_frame or LZ4F_skippableFrame</summary>
    public FrameType FrameType { get => frameType; init => frameType = value; }
    /// <summary>Size of uncompressed content ; 0 == unknown</summary>
    public ulong ContentSize { get => contentSize; init => contentSize = value; }
    /// <summary>Dictionary ID, sent by compressor to help decoder select correct dictionary; 0 == no dictID provided</summary>
    public uint DictionaryID { get => dictionaryID; init => dictionaryID = value; }
    /// <summary>1: each block followed by a checksum of block's compressed data; 0 == default (disabled)</summary>
    public BlockChecksum BlockChecksumFlag { get => blockChecksumFlag; init => blockChecksumFlag = value; }
}

public enum BlockSizeId : int
{
    Default = 0,
    Max64KB = 4,
    Max256KB = 5,
    Max1MB = 6,
    Max4MB = 7
}

public enum BlockMode : int
{
    BlockLinked = 0,
    BlockIndependent = 1
}

public enum ContentChecksum : int
{
    NoContentChecksum = 0,
    ContentChecksumEnabled = 1
}

public enum BlockChecksum : int
{
    NoBlockChecksum = 0,
    BlockChecksumEnabled = 1
}

public enum FrameType : int
{
    Frame = 0,
    SkippableFrame = 1
}
