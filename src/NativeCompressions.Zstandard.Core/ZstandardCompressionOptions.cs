using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeCompressions.Interop;
using static NativeCompressions.Interop.ZstandardNativeMethods;

namespace NativeCompressions;

[StructLayout(LayoutKind.Auto)]
public readonly record struct ZstandardCompressionOptions
{
    public static readonly ZstandardCompressionOptions Default = new ZstandardCompressionOptions();

    public bool IsDefault
    {
        get
        {
#if NETSTANDARD2_0
            return Equals(Default); // no MemoryMarshal.CreateReadOnlySpan, the generated field-wise comparison is fine here
#else
            var thisSpan = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<ZstandardCompressionOptions, byte>(ref Unsafe.AsRef(in this)),
                Unsafe.SizeOf<ZstandardCompressionOptions>());

            var defaultSpan = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<ZstandardCompressionOptions, byte>(ref Unsafe.AsRef(in Default)),
                Unsafe.SizeOf<ZstandardCompressionOptions>());

            return thisSpan.SequenceEqual(defaultSpan);
#endif
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

    readonly ZstandardDictionary? dictionary;

    public ZstandardCompressionOptions()
    {
    }

    public ZstandardCompressionOptions(int compressionLevel)
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

    public ZstandardDictionary? Dictionary
    {
        get => dictionary;
        init => dictionary = value;
    }

    internal ZstandardDictionary.Lease AcquireDictionary() => dictionary == null ? default : dictionary.Acquire();

    // The lease is the one taken on the dictionary of these options.
    internal unsafe void SetParameter(ZSTD_CCtx_s* context, in ZstandardDictionary.Lease lease)
    {
        SetParameters(context);

        if (!lease.IsEmpty)
        {
            var result = ZSTD_CCtx_refCDict(context, lease.Compression);
            Zstandard.ThrowIfError(result);
        }
    }

    unsafe void SetParameters(ZSTD_CCtx_s* context)
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
    }

    // Set parameter if value is not zero(default).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void SetParameter(ZSTD_CCtx_s* context, ZSTD_cParameter parameter, int value)
    {
        if (value != 0)
        {
            var code = ZSTD_CCtx_setParameter(context, parameter, value);
            Zstandard.ThrowIfError(code);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void SetParameterDefaultIsTrue(ZSTD_CCtx_s* context, ZSTD_cParameter parameter, bool value)
    {
        if (!value)
        {
            var code = ZSTD_CCtx_setParameter(context, parameter, 0); // set to false
            Zstandard.ThrowIfError(code);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void SetParameterDefaultIsFalse(ZSTD_CCtx_s* context, ZSTD_cParameter parameter, bool value)
    {
        if (value)
        {
            var code = ZSTD_CCtx_setParameter(context, parameter, 1); // set to true
            Zstandard.ThrowIfError(code);
        }
    }
}
