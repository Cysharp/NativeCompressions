using NativeCompressions.ZStandard.Raw;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static NativeCompressions.ZStandard.Raw.NativeMethods;

namespace NativeCompressions.ZStandard;

// TODO: Add ZStandardDecompressionOptions

/// <summary>
/// Represents as ZSTD_cParameter.
/// </summary>
public readonly record struct ZStandardCompressionOptions
{
    public static readonly ZStandardCompressionOptions Default = new ZStandardCompressionOptions();

    public bool IsDefault => this == Default; // TODO: memory compare

    // enum ZSTD_cParameter

    readonly int compressionLevel;
    readonly int windowLog;
    readonly int hashLog;
    readonly int chainLog;
    readonly int searchLog;
    readonly int minMatch;
    readonly int targetLength;
    readonly int strategy;
    readonly int enableLongDistanceMatching;
    readonly int ldmHashLog;
    readonly int ldmMinMatch;
    readonly int ldmBucketSizeLog;
    readonly int ldmHashRateLog;
    readonly int contentSizeFlag;
    readonly int checksumFlag;
    readonly int dictIDFlag;
    readonly int nbWorkers;
    readonly int jobSize;
    readonly int overlapLog;

    public ZStandardCompressionOptions()
    {
        // default is 1
        contentSizeFlag = 1;
        dictIDFlag = 1;
    }

    /// <summary>
    /// Compression level, with the following ranges:
    /// - Negative values: ultra-fast compression (up to -131072)
    /// - 0: default compression level (currently 3)
    /// - 1-22: standard compression levels
    /// - Higher levels = better compression ratio but slower
    /// </summary>
    public int CompressionLevel
    {
        get => compressionLevel;
        init => compressionLevel = value;
    }

    /// <summary>
    /// Window log size (10-31). Larger values use more memory but may improve compression ratio.
    /// 0 = use default
    /// </summary>
    public int WindowLog { get; init; }

    // TODO: hashLog, chainLog, searchLog

    /// <summary>
    /// Minimum match length (3-7). Smaller values may improve compression speed.
    /// 0 = use default
    /// </summary>
    public int MinMatch { get; init; }

    // ZSTD_c_targetLength
    // ZSTD_c_strategy

    /// <summary>
    /// Enable long distance matching. 0 = disabled, 1 = enabled
    /// </summary>
    public bool EnableLongDistanceMatching { get; init; }

    //ZSTD_c_ldmHashLog
    //ZSTD_c_ldmMinMatch
    //ZSTD_c_ldmBucketSizeLog
    //ZSTD_c_ldmHashRateLog

    // TODO:contentSizeFlag default = 1

    /// <summary>
    /// Include content size in frame header. 0 = no, 1 = yes
    /// </summary>
    public int ContentSizeFlag { get; init; }

    // TODO:checksum default is 0

    /// <summary>
    /// Include checksum at the end of frame. 0 = no checksum, 1 = include checksum
    /// </summary>
    public int ChecksumFlag { get; init; }

    // TODO:deafult is 1

    /// <summary>
    /// Include dictionary ID in frame header when using dictionary compression
    /// </summary>
    public int DictIDFlag { get; init; }

    // Default value is `0`, aka "single-threaded mode" : no worker is spawned,

    /// <summary>
    /// Number of worker threads for multi-threaded compression. 0 = single-threaded
    /// </summary>
    public int NbWorkers { get; init; }

    /// <summary>
    /// Size of job (in bytes). 0 = automatic
    /// </summary>
    public int JobSize { get; init; }

    // ZSTD_c_overlapLog

    internal unsafe void SetParameter(ZSTD_CCtx_s* context)
    {
        if (IsDefault) return;

        // TODO: check for almostly default(change only compression level)

        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_compressionLevel, compressionLevel);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_hashLog, hashLog);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_chainLog, chainLog);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_searchLog, searchLog);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_minMatch, minMatch);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_targetLength, targetLength);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_strategy, strategy);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, enableLongDistanceMatching);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_ldmHashLog, ldmHashLog);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_ldmMinMatch, ldmMinMatch);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_ldmBucketSizeLog, ldmBucketSizeLog);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_ldmHashRateLog, ldmHashRateLog);
        SetParameterCoreDefaultIsOne(context, ZSTD_cParameter.ZSTD_c_contentSizeFlag, contentSizeFlag);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_checksumFlag, checksumFlag);
        SetParameterCoreDefaultIsOne(context, ZSTD_cParameter.ZSTD_c_dictIDFlag, dictIDFlag);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_nbWorkers, nbWorkers);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_jobSize, jobSize);
        SetParameterCore(context, ZSTD_cParameter.ZSTD_c_overlapLog, overlapLog);
    }

    // Set parameter if value is not zero(default).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe void SetParameterCore(ZSTD_CCtx_s* context, ZSTD_cParameter parameter, int value)
    {
        if (value != 0)
        {
            var code = ZSTD_CCtx_setParameter(context, (int)parameter, value);
            ZStandard.ThrowIfError(code);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe void SetParameterCoreDefaultIsOne(ZSTD_CCtx_s* context, ZSTD_cParameter parameter, int value)
    {
        if (value != 1)
        {
            var code = ZSTD_CCtx_setParameter(context, (int)parameter, value);
            if (ZStandard.IsError(code)) // for inlining
            {
                ZStandard.ThrowIfError(code);
            }
        }
    }
}

// Enums for ZStandard parameters
internal enum ZSTD_cParameter
{
    ZSTD_c_compressionLevel = 100,
    ZSTD_c_windowLog = 101,
    ZSTD_c_hashLog = 102,
    ZSTD_c_chainLog = 103,
    ZSTD_c_searchLog = 104,
    ZSTD_c_minMatch = 105,
    ZSTD_c_targetLength = 106,
    ZSTD_c_strategy = 107,
    ZSTD_c_enableLongDistanceMatching = 160,
    ZSTD_c_ldmHashLog = 161,
    ZSTD_c_ldmMinMatch = 162,
    ZSTD_c_ldmBucketSizeLog = 163,
    ZSTD_c_ldmHashRateLog = 164,
    ZSTD_c_contentSizeFlag = 200,
    ZSTD_c_checksumFlag = 201,
    ZSTD_c_dictIDFlag = 202,
    ZSTD_c_nbWorkers = 400,
    ZSTD_c_jobSize = 401,
    ZSTD_c_overlapLog = 402
}
