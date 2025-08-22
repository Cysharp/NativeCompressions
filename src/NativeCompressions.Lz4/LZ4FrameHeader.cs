using NativeCompressions.LZ4.Raw;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NativeCompressions.LZ4;

// LZ4FrameOptions is NativeMethods.LZ4F_preferences_t
// LZ4FrameInfo is NativeMethods.LZ4F_frameInfo_t

// TODO: make record and internal ToOptions...?
[StructLayout(LayoutKind.Sequential)]
public unsafe struct LZ4FrameOptions
{
    public static readonly LZ4FrameOptions Default = new LZ4FrameOptions();

    public LZ4FrameInfo FrameInfo;
    /// <summary>0: default (fast mode); values > LZ4HC_CLEVEL_MAX count as LZ4HC_CLEVEL_MAX; values < 0 trigger "fast acceleration"</summary>
    public int CompressionLevel;
    /// <summary>1: always flush; reduces usage of internal buffers</summary>
    private uint autoFlush; // changed to private
    /// <summary>parser favors decompression speed vs compression ratio. Only works for high compression modes (>= LZ4HC_CLEVEL_OPT_MIN)</summary>
    public uint FavorDecompressionSpeed;
    private fixed uint reserved[3]; // changed to private

    public bool AutoFlush
    {
        get => autoFlush == 1;
        set
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

    internal LZ4F_preferences_t* ToPreferences()
    {
        ref var self = ref Unsafe.As<LZ4FrameOptions, LZ4F_preferences_t>(ref this);
        var ptr = Unsafe.AsPointer(ref self);
        return (LZ4F_preferences_t*)ptr;
    }
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LZ4FrameInfo
{
    /// <summary>max64KB, max256KB, max1MB, max4MB; 0 == default (LZ4F_max64KB)</summary>
    public BlockSizeId BlockSizeID;
    /// <summary>LZ4F_blockLinked, LZ4F_blockIndependent; 0 == default (LZ4F_blockLinked)</summary>
    public BlockMode BlockMode;
    /// <summary>1: add a 32-bit checksum of frame's decompressed data; 0 == default (disabled)</summary>
    public ContentChecksum ContentChecksumFlag;
    /// <summary>LZ4F_frame or LZ4F_skippableFrame</summary>
    public FrameType FrameType;
    /// <summary>Size of uncompressed content ; 0 == unknown</summary>
    public ulong ContentSize;
    /// <summary>Dictionary ID, sent by compressor to help decoder select correct dictionary; 0 == no dictID provided</summary>
    public uint DictionaryID;
    /// <summary>1: each block followed by a checksum of block's compressed data; 0 == default (disabled)</summary>
    public BlockChecksum BlockChecksumFlag;
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
    BlockIndepent = 1
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