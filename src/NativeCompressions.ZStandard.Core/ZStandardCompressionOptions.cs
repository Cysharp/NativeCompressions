using NativeCompressions.ZStandard.Raw;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static NativeCompressions.ZStandard.Raw.NativeMethods;

namespace NativeCompressions.ZStandard;

/// <summary>
/// Represents as ZSTD_cParameter.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ZStandardCompressionOptions
{
    public static readonly ZStandardCompressionOptions Default = new ZStandardCompressionOptions();

    public bool IsDefault
    {
        get
        {
            var thisSpan = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<ZStandardCompressionOptions, byte>(ref Unsafe.AsRef(in this)),
                Unsafe.SizeOf<ZStandardCompressionOptions>());

            var defaultSpan = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<ZStandardCompressionOptions, byte>(ref Unsafe.AsRef(in Default)),
                Unsafe.SizeOf<ZStandardCompressionOptions>());

            return thisSpan.SequenceEqual(defaultSpan);
        }
    }

    readonly int compressionLevel;
    readonly int windowLog;
    readonly int hashLog;
    readonly int chainLog;
    readonly int searchLog;
    readonly int minMatch;
    readonly int targetLength;
    readonly int strategy;
    readonly bool enableLongDistanceMatching = false; // int to bool, default: 0
    readonly int ldmHashLog;
    readonly int ldmMinMatch;
    readonly int ldmBucketSizeLog;
    readonly int ldmHashRateLog;
    readonly bool contentSizeFlag = true; // int to bool, default: 1
    readonly bool checksumFlag = false;   // int to bool, default: 0
    readonly bool dictIDFlag = true;      // int to bool, default: 1
    readonly int nbWorkers;
    readonly int jobSize;
    readonly int overlapLog;

    public ZStandardCompressionOptions()
    {
    }

    public ZStandardCompressionOptions(int compressionLevel)
    {
        this.compressionLevel = compressionLevel;
    }

    // mapped from zstd.h ZSTD_cParameter

    /// <summary>
    /// Set compression parameters according to pre-defined cLevel table.
    /// Note that exact compression parameters are dynamically determined,
    /// depending on both compression level and srcSize (when known).
    /// Default level is ZSTD_CLEVEL_DEFAULT==3.
    /// Special: value 0 means default, which is controlled by ZSTD_CLEVEL_DEFAULT.
    /// Note 1 : it's possible to pass a negative compression level.
    /// Note 2 : setting a level does not automatically set all other compression parameters
    ///   to default. Setting this will however eventually dynamically impact the compression
    ///   parameters which have not been manually set. The manually set
    ///   ones will 'stick'.
    /// </summary>
    public int CompressionLevel
    {
        get => compressionLevel;
        init => compressionLevel = value;
    }

    /// <summary>
    /// Maximum allowed back-reference distance, expressed as power of 2.
    /// This will set a memory budget for streaming decompression,
    /// with larger values requiring more memory
    /// and typically compressing more.
    /// Must be clamped between ZSTD_WINDOWLOG_MIN and ZSTD_WINDOWLOG_MAX.
    /// Special: value 0 means "use default windowLog".
    /// Note: Using a windowLog greater than ZSTD_WINDOWLOG_LIMIT_DEFAULT
    /// requires explicitly allowing such size at streaming decompression stage.
    /// </summary>
    public int WindowLog
    {
        get => windowLog;
        init => windowLog = value;
    }

    /// <summary>
    /// Size of the initial probe table, as a power of 2.
    /// Resulting memory usage is (1 &lt;&lt; (hashLog+2)).
    /// Must be clamped between ZSTD_HASHLOG_MIN and ZSTD_HASHLOG_MAX.
    /// Larger tables improve compression ratio of strategies &lt;= dFast,
    /// and improve speed of strategies &gt; dFast.
    /// Special: value 0 means "use default hashLog".
    /// </summary>
    public int HashLog
    {
        get => hashLog;
        init => hashLog = value;
    }

    /// <summary>
    /// Size of the multi-probe search table, as a power of 2.
    /// Resulting memory usage is (1 &lt;&lt; (chainLog+2)).
    /// Must be clamped between ZSTD_CHAINLOG_MIN and ZSTD_CHAINLOG_MAX.
    /// Larger tables result in better and slower compression.
    /// This parameter is useless for "fast" strategy.
    /// It's still useful when using "dfast" strategy,
    /// in which case it defines a secondary probe table.
    /// Special: value 0 means "use default chainLog".
    /// </summary>
    public int ChainLog
    {
        get => chainLog;
        init => chainLog = value;
    }

    /// <summary>
    /// Number of search attempts, as a power of 2.
    /// More attempts result in better and slower compression.
    /// This parameter is useless for "fast" and "dFast" strategies.
    /// Special: value 0 means "use default searchLog".
    /// </summary>
    public int SearchLog
    {
        get => searchLog;
        init => searchLog = value;
    }

    /// <summary>
    /// Minimum size of searched matches.
    /// Note that Zstandard can still find matches of smaller size,
    /// it just tweaks its search algorithm to look for this size and larger.
    /// Larger values increase compression and decompression speed, but decrease ratio.
    /// Must be clamped between ZSTD_MINMATCH_MIN and ZSTD_MINMATCH_MAX.
    /// Note that currently, for all strategies &lt; btopt, effective minimum is 4.
    ///                    , for all strategies &gt; fast, effective maximum is 6.
    /// Special: value 0 means "use default minMatchLength".
    /// </summary>
    public int MinMatch
    {
        get => minMatch;
        init => minMatch = value;
    }

    /// <summary>
    /// Impact of this field depends on strategy.
    /// For strategies btopt, btultra &amp; btultra2:
    ///     Length of Match considered "good enough" to stop search.
    ///     Larger values make compression stronger, and slower.
    /// For strategy fast:
    ///     Distance between match sampling.
    ///     Larger values make compression faster, and weaker.
    /// Special: value 0 means "use default targetLength".
    /// </summary>
    public int TargetLength
    {
        get => targetLength;
        init => targetLength = value;
    }

    /// <summary>
    /// See ZSTD_strategy enum definition.
    /// The higher the value of selected strategy, the more complex it is,
    /// resulting in stronger and slower compression.
    /// Special: value 0 means "use default strategy".
    /// </summary>
    public int Strategy
    {
        get => strategy;
        init => strategy = value;
    }

    // LDM(long distance matching) mode parameters

    /// <summary>
    /// Enable long distance matching.
    /// This parameter is designed to improve compression ratio
    /// for large inputs, by finding large matches at long distance.
    /// It increases memory usage and window size.
    /// Note: enabling this parameter increases default ZSTD_c_windowLog to 128 MB
    /// except when expressly set to a different value.
    /// Note: will be enabled by default if ZSTD_c_windowLog &gt;= 128 MB and
    /// compression strategy &gt;= ZSTD_btopt (== compression level 16+)
    /// </summary>
    public bool EnableLongDistanceMatching
    {
        get => enableLongDistanceMatching;
        init => enableLongDistanceMatching = value;
    }

    /// <summary>
    /// Size of the table for long distance matching, as a power of 2.
    /// Larger values increase memory usage and compression ratio,
    /// but decrease compression speed.
    /// Must be clamped between ZSTD_HASHLOG_MIN and ZSTD_HASHLOG_MAX
    /// default: windowlog - 7.
    /// Special: value 0 means "automatically determine hashlog".
    /// </summary>
    public int LdmHashLog
    {
        get => ldmHashLog;
        init => ldmHashLog = value;
    }

    /// <summary>
    /// Minimum match size for long distance matcher.
    /// Larger/too small values usually decrease compression ratio.
    /// Must be clamped between ZSTD_LDM_MINMATCH_MIN and ZSTD_LDM_MINMATCH_MAX.
    /// Special: value 0 means "use default value" (default: 64).
    /// </summary>
    public int LdmMinMatch
    {
        get => ldmMinMatch;
        init => ldmMinMatch = value;
    }

    /// <summary>
    /// Log size of each bucket in the LDM hash table for collision resolution.
    /// Larger values improve collision resolution but decrease compression speed.
    /// The maximum value is ZSTD_LDM_BUCKETSIZELOG_MAX.
    /// Special: value 0 means "use default value" (default: 3).
    /// </summary>
    public int LdmBucketSizeLog
    {
        get => ldmBucketSizeLog;
        init => ldmBucketSizeLog = value;
    }

    /// <summary>
    /// Frequency of inserting/looking up entries into the LDM hash table.
    /// Must be clamped between 0 and (ZSTD_WINDOWLOG_MAX - ZSTD_HASHLOG_MIN).
    /// Default is MAX(0, (windowLog - ldmHashLog)), optimizing hash table usage.
    /// Larger values improve compression speed.
    /// Deviating far from default value will likely result in a compression ratio decrease.
    /// Special: value 0 means "automatically determine hashRateLog".
    /// </summary>
    public int LdmHashRateLog
    {
        get => ldmHashRateLog;
        init => ldmHashRateLog = value;
    }

    // frame parameters

    /// <summary>
    /// Content size will be written into frame header _whenever known_ (default:true)
    /// Content size must be known at the beginning of compression.
    /// This is automatically the case when using ZSTD_compress2(),
    /// For streaming scenarios, content size must be provided with ZSTD_CCtx_setPledgedSrcSize()
    /// </summary>
    public bool ContentSizeFlag
    {
        get => contentSizeFlag;
        init => contentSizeFlag = value;
    }

    /// <summary>
    /// A 32-bits checksum of content is written at end of frame (default:false)
    /// </summary>
    public bool ChecksumFlag
    {
        get => checksumFlag;
        init => checksumFlag = value;
    }

    /// <summary>
    /// When applicable, dictionary's ID is written into frame header (default:true)
    /// </summary>
    public bool DictIDFlag
    {
        get => dictIDFlag;
        init => dictIDFlag = value;
    }

    // multi-threading parameters
    // These parameters are only active if multi-threading is enabled (compiled with build macro ZSTD_MULTITHREAD).
    // Otherwise, trying to set any other value than default (0) will be a no-op and return an error.
    // In a situation where it's unknown if the linked library supports multi-threading or not,
    // setting ZSTD_c_nbWorkers to any value >= 1 and consulting the return value provides a quick way to check this property.

    /// <summary>
    /// Select how many threads will be spawned to compress in parallel.
    /// When nbWorkers &gt;= 1, triggers asynchronous mode when invoking ZSTD_compressStream*() :
    /// ZSTD_compressStream*() consumes input and flush output if possible, but immediately gives back control to caller,
    /// while compression is performed in parallel, within worker thread(s).
    /// (note : a strong exception to this rule is when first invocation of ZSTD_compressStream2() sets ZSTD_e_end :
    ///  in which case, ZSTD_compressStream2() delegates to ZSTD_compress2(), which is always a blocking call).
    /// More workers improve speed, but also increase memory usage.
    /// Default value is `0`, aka "single-threaded mode" : no worker is spawned,
    /// compression is performed inside Caller's thread, and all invocations are blocking
    /// </summary>
    public int NbWorkers
    {
        get => nbWorkers;
        init => nbWorkers = value;
    }

    /// <summary>
    /// Size of a compression job. This value is enforced only when nbWorkers &gt;= 1.
    /// Each compression job is completed in parallel, so this value can indirectly impact the nb of active threads.
    /// 0 means default, which is dynamically determined based on compression parameters.
    /// Job size must be a minimum of overlap size, or ZSTDMT_JOBSIZE_MIN (= 512 KB), whichever is largest.
    /// The minimum size is automatically and transparently enforced.
    /// </summary>
    public int JobSize
    {
        get => jobSize;
        init => jobSize = value;
    }

    /// <summary>
    /// Control the overlap size, as a fraction of window size.
    /// The overlap size is an amount of data reloaded from previous job at the beginning of a new job.
    /// It helps preserve compression ratio, while each job is compressed in parallel.
    /// This value is enforced only when nbWorkers &gt;= 1.
    /// Larger values increase compression ratio, but decrease speed.
    /// Possible values range from 0 to 9 :
    /// - 0 means "default" : value will be determined by the library, depending on strategy
    /// - 1 means "no overlap"
    /// - 9 means "full overlap", using a full window size.
    /// Each intermediate rank increases/decreases load size by a factor 2 :
    /// 9: full window;  8: w/2;  7: w/4;  6: w/8;  5:w/16;  4: w/32;  3:w/64;  2:w/128;  1:no overlap;  0:default
    /// default value varies between 6 and 9, depending on strategy
    /// </summary>
    public int OverlapLog
    {
        get => overlapLog;
        init => overlapLog = value;
    }

    internal unsafe void SetParameter(ZSTD_CCtx_s* context)
    {
        if (IsDefault) return;

        SetParameter(context, ZSTD_cParameter.ZSTD_c_compressionLevel, compressionLevel);
        SetParameter(context, ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
        SetParameter(context, ZSTD_cParameter.ZSTD_c_hashLog, hashLog);
        SetParameter(context, ZSTD_cParameter.ZSTD_c_chainLog, chainLog);
        SetParameter(context, ZSTD_cParameter.ZSTD_c_searchLog, searchLog);
        SetParameter(context, ZSTD_cParameter.ZSTD_c_minMatch, minMatch);
        SetParameter(context, ZSTD_cParameter.ZSTD_c_targetLength, targetLength);
        SetParameter(context, ZSTD_cParameter.ZSTD_c_strategy, strategy);
        SetParameterDefaultIsFalse(context, ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, enableLongDistanceMatching);
        SetParameter(context, ZSTD_cParameter.ZSTD_c_ldmHashLog, ldmHashLog);
        SetParameter(context, ZSTD_cParameter.ZSTD_c_ldmMinMatch, ldmMinMatch);
        SetParameter(context, ZSTD_cParameter.ZSTD_c_ldmBucketSizeLog, ldmBucketSizeLog);
        SetParameter(context, ZSTD_cParameter.ZSTD_c_ldmHashRateLog, ldmHashRateLog);
        SetParameterDefaultIsTrue(context, ZSTD_cParameter.ZSTD_c_contentSizeFlag, contentSizeFlag);
        SetParameterDefaultIsFalse(context, ZSTD_cParameter.ZSTD_c_checksumFlag, checksumFlag);
        SetParameterDefaultIsTrue(context, ZSTD_cParameter.ZSTD_c_dictIDFlag, dictIDFlag);
        SetParameter(context, ZSTD_cParameter.ZSTD_c_nbWorkers, nbWorkers);
        SetParameter(context, ZSTD_cParameter.ZSTD_c_jobSize, jobSize);
        SetParameter(context, ZSTD_cParameter.ZSTD_c_overlapLog, overlapLog);
    }

    // Set parameter if value is not zero(default).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void SetParameter(ZSTD_CCtx_s* context, ZSTD_cParameter parameter, int value)
    {
        if (value != 0)
        {
            var code = ZSTD_CCtx_setParameter(context, (int)parameter, value);
            if (ZStandard.IsError(code)) // for inlining
            {
                ZStandard.ThrowAsError(code);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void SetParameterDefaultIsTrue(ZSTD_CCtx_s* context, ZSTD_cParameter parameter, bool value)
    {
        if (!value)
        {
            var code = ZSTD_CCtx_setParameter(context, (int)parameter, 0); // set to false
            if (ZStandard.IsError(code)) // for inlining
            {
                ZStandard.ThrowAsError(code);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void SetParameterDefaultIsFalse(ZSTD_CCtx_s* context, ZSTD_cParameter parameter, bool value)
    {
        if (value)
        {
            var code = ZSTD_CCtx_setParameter(context, (int)parameter, 1); // set to true
            if (ZStandard.IsError(code)) // for inlining
            {
                ZStandard.ThrowAsError(code);
            }
        }
    }

    enum ZSTD_cParameter
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
}
