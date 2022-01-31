using size_t = System.UIntPtr; // nuint is .NET size_t equivalent, internally nuint is represent as UIntPtr.
using unsigned = System.UInt32;

using System.Runtime.InteropServices;
using System.Reflection;

namespace NativeCompressions.ZStandard;

// v1.5.1 Manual
// http://facebook.github.io/zstd/zstd_manual.html

/* 
  zstd, short for Zstandard, is a fast lossless compression algorithm, targeting
  real-time compression scenarios at zlib-level and better compression ratios.
  The zstd compression library provides in-memory compression and decompression
  functions.

  The library supports regular compression levels from 1 up to ZSTD_maxCLevel(),
  which is currently 22. Levels >= 20, labeled `--ultra`, should be used with
  caution, as they require more memory. The library also offers negative
  compression levels, which extend the range of speed vs. ratio preferences.
  The lower the level, the faster the speed (at the cost of compression).

  Compression can be done in:
    - a single step (described as Simple API)
    - a single step, reusing a context (described as Explicit context)
    - unbounded multiple steps (described as Streaming compression)

  The compression ratio achievable on small data can be highly improved using
  a dictionary. Dictionary compression can be performed in:
    - a single step (described as Simple dictionary API)
    - a single step, reusing a dictionary (described as Bulk-processing
      dictionary API)

  Advanced experimental functions can be accessed using
  `#define ZSTD_STATIC_LINKING_ONLY` before including zstd.h.

  Advanced experimental APIs should never be used with a dynamically-linked
  library. They are not "stable"; their definitions or signatures may change in
  the future. Only static linking is allowed.
*/
public static unsafe class LibZstd // NativeMethods
{
    // https://docs.microsoft.com/en-us/dotnet/standard/native-interop/cross-platform
    // will search
    // win => libzstd, libzstd.dll
    // linux, osx => libzstd.so, libzstd.dylib
    const string LibZstdDll = "libzstd";

    static LibZstd()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibZstd).Assembly, DllImportResolver);
    }

    static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == LibZstdDll)
        {
            var path = "runtimes/";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                path += "win-";

            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                path += "osx-";
            }
            else
            {
                path += "linux-";
            }

            if (RuntimeInformation.OSArchitecture == Architecture.X86)
            {
                path += "x86";
            }
            else if (RuntimeInformation.OSArchitecture == Architecture.X64)
            {
                path += "x64";
            }
            else if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
            {
                path += "arm64";
            }

            path += "/native/" + LibZstdDll;

            return NativeLibrary.Load(path, assembly, searchPath);
        }

        return IntPtr.Zero;
    }

    #region Version

    /// <summary>
    /// Return runtime library version, the value is (MAJOR*100*100 + MINOR*100 + RELEASE). 
    /// </summary>
    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsigned ZSTD_versionNumber();

    /// <summary>
    /// Return runtime library version, like "1.4.5". Requires v1.3.0+.
    /// </summary>
    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* ZSTD_versionString();

    #endregion

    #region Simple API

    /// <summary>
    /// Compresses `src` content as a single zstd compressed frame into already allocated `dst`.
    /// Hint : compression runs faster if `dstCapacity` >=  `ZSTD_compressBound(srcSize)`.
    /// @return : compressed size written into `dst` (<= `dstCapacity),
    /// or an error code if it fails(which can be tested using ZSTD_isError())
    /// </summary>
    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern size_t ZSTD_compress(void* dst, size_t dstCapacity, void* src, size_t srcSize, int compressionLevel);

    /// <summary>
    /// `compressedSize` : must be the _exact_ size of some number of compressed and/or skippable frames.
    /// `dstCapacity` is an upper bound of originalSize to regenerate.
    /// If user cannot imply a maximum upper bound, it's better to use streaming mode to decompress data.
    /// @return : the number of bytes decompressed into `dst` (<= `dstCapacity`),
    /// or an errorCode if it fails(which can be tested using ZSTD_isError()). 
    /// </summary>
    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern size_t ZSTD_decompress(void* dst, size_t dstCapacity, void* src, size_t compressedSize);

    public const ulong ZSTD_CONTENTSIZE_UNKNOWN = unchecked(0UL - 1);
    public const ulong ZSTD_CONTENTSIZE_ERROR = unchecked(0UL - 2);

    /// <summary>
    /// `src` should point to the start of a ZSTD encoded frame.
    /// `srcSize` must be at least as large as the frame header.
    /// hint : any size >= `ZSTD_frameHeaderSize_max` is large enough.
    /// @return : - decompressed size of `src` frame content, if known
    /// - ZSTD_CONTENTSIZE_UNKNOWN if the size cannot be determined
    /// - ZSTD_CONTENTSIZE_ERROR if an error occurred (e.g.invalid magic number, srcSize too small)
    /// note 1 : a 0 return value means the frame is valid but "empty".
    /// note 2 : decompressed size is an optional field, it may not be present, typically in streaming mode.
    ///          When `return==ZSTD_CONTENTSIZE_UNKNOWN`, data to decompress could be any size.
    ///          In which case, it's necessary to use streaming mode to decompress data.
    ///          Optionally, application can rely on some implicit limit,
    ///          as ZSTD_decompress() only needs an upper bound of decompressed size.
    ///          (For example, data could be necessarily cut into blocks <= 16 KB).
    /// note 3 : decompressed size is always present when compression is completed using single-pass functions,
    ///          such as ZSTD_compress(), ZSTD_compressCCtx() ZSTD_compress_usingDict() or ZSTD_compress_usingCDict().
    /// note 4 : decompressed size can be very large (64-bits value),
    ///          potentially larger than what local system can handle as a single memory segment.
    ///          In which case, it's necessary to use streaming mode to decompress data.
    /// note 5 : If source is untrusted, decompressed size could be wrong or intentionally modified.
    ///          Always ensure return value fits within application's authorized limits.
    ///          Each application can set its own limits.
    /// note 6 : This function replaces ZSTD_getDecompressedSize() 
    /// </summary>
    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong ZSTD_getFrameContentSize(void* src, size_t srcSize);

    // Obsolete, use ZSTD_getFrameContentSize instead.
    // unsigned long long ZSTD_getDecompressedSize(const void* src, size_t srcSize);

    /// <summary>
    ///  `src` should point to the start of a ZSTD frame or skippable frame.
    /// `srcSize` must be >= first frame size
    ///  @return : the compressed size of the first frame starting at `src`,
    /// suitable to pass as `srcSize` to `ZSTD_decompress` or similar,
    /// or an error code if input is invalid
    /// </summary>
    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern size_t ZSTD_findFrameCompressedSize(void* src, size_t srcSize);

    #endregion

    #region Helper functions

    /// <summary>
    /// maximum compressed size in worst case single-pass scenario
    /// </summary>
    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern size_t ZSTD_compressBound(size_t srcSize);

    /// <summary>
    /// tells if a `size_t` function result is an error code
    /// </summary>
    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsigned ZSTD_isError(size_t code);

    /// <summary>
    /// provides readable string from an error code
    /// </summary>
    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* ZSTD_getErrorName(size_t code);

    /// <summary>
    /// minimum negative compression level allowed, requires v1.4.0+
    /// </summary>
    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ZSTD_minCLevel();

    /// <summary>
    /// maximum compression level available
    /// </summary>
    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ZSTD_maxCLevel();

    /// <summary>
    /// default compression level, specified by ZSTD_CLEVEL_DEFAULT, requires v1.5.0+
    /// </summary>
    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ZSTD_defaultCLevel();

    #endregion

    #region Explicit context

    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void* ZSTD_createCCtx();

    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern size_t ZSTD_freeCCtx(void* cctx);


    /// <summary>
    /// Same as ZSTD_compress(), using an explicit ZSTD_CCtx.
    /// Important : in order to behave similarly to `ZSTD_compress()`,
    /// this function compresses at requested compression level,
    /// __ignoring any other parameter__.
    /// If any advanced parameter was set using the advanced API,
    /// they will all be reset. Only `compressionLevel` remains.
    /// </summary>
    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern size_t ZSTD_compressCCtx(void* cctx, void* dst, size_t dstCapacity, void* src, size_t srcSize, int compressionLevel);

    // TODO: decompressContext

    #endregion

    #region Advanced compression API

    public enum ZSTD_strategy
    {
        ZSTD_fast = 1,
        ZSTD_dfast = 2,
        ZSTD_greedy = 3,
        ZSTD_lazy = 4,
        ZSTD_lazy2 = 5,
        ZSTD_btlazy2 = 6,
        ZSTD_btopt = 7,
        ZSTD_btultra = 8,
        ZSTD_btultra2 = 9
        /* note : new strategies _might_ be added in the future.
                  Only the order (from fast to strong) is guaranteed */
    }

    /// <summary>
    /// compression parameters
    /// Note: When compressing with a ZSTD_CDict these parameters are superseded
    /// by the parameters used to construct the ZSTD_CDict.
    /// See ZSTD_CCtx_refCDict() for more info (superseded-by-cdict).
    /// </summary>
    public enum ZSTD_cParameter
    {
        ZSTD_c_compressionLevel = 100, /* Set compression parameters according to pre-defined cLevel table.
                              * Note that exact compression parameters are dynamically determined,
                              * depending on both compression level and srcSize (when known).
                              * Default level is ZSTD_CLEVEL_DEFAULT==3.
                              * Special: value 0 means default, which is controlled by ZSTD_CLEVEL_DEFAULT.
                              * Note 1 : it's possible to pass a negative compression level.
                              * Note 2 : setting a level does not automatically set all other compression parameters
                              *   to default. Setting this will however eventually dynamically impact the compression
                              *   parameters which have not been manually set. The manually set
                              *   ones will 'stick'. */
        /* Advanced compression parameters :
         * It's possible to pin down compression parameters to some specific values.
         * In which case, these values are no longer dynamically selected by the compressor */
        ZSTD_c_windowLog = 101,    /* Maximum allowed back-reference distance, expressed as power of 2.
                              * This will set a memory budget for streaming decompression,
                              * with larger values requiring more memory
                              * and typically compressing more.
                              * Must be clamped between ZSTD_WINDOWLOG_MIN and ZSTD_WINDOWLOG_MAX.
                              * Special: value 0 means "use default windowLog".
                              * Note: Using a windowLog greater than ZSTD_WINDOWLOG_LIMIT_DEFAULT
                              *       requires explicitly allowing such size at streaming decompression stage. */
        ZSTD_c_hashLog = 102,      /* Size of the initial probe table, as a power of 2.
                              * Resulting memory usage is (1 << (hashLog+2)).
                              * Must be clamped between ZSTD_HASHLOG_MIN and ZSTD_HASHLOG_MAX.
                              * Larger tables improve compression ratio of strategies <= dFast,
                              * and improve speed of strategies > dFast.
                              * Special: value 0 means "use default hashLog". */
        ZSTD_c_chainLog = 103,     /* Size of the multi-probe search table, as a power of 2.
                              * Resulting memory usage is (1 << (chainLog+2)).
                              * Must be clamped between ZSTD_CHAINLOG_MIN and ZSTD_CHAINLOG_MAX.
                              * Larger tables result in better and slower compression.
                              * This parameter is useless for "fast" strategy.
                              * It's still useful when using "dfast" strategy,
                              * in which case it defines a secondary probe table.
                              * Special: value 0 means "use default chainLog". */
        ZSTD_c_searchLog = 104,    /* Number of search attempts, as a power of 2.
                              * More attempts result in better and slower compression.
                              * This parameter is useless for "fast" and "dFast" strategies.
                              * Special: value 0 means "use default searchLog". */
        ZSTD_c_minMatch = 105,     /* Minimum size of searched matches.
                              * Note that Zstandard can still find matches of smaller size,
                              * it just tweaks its search algorithm to look for this size and larger.
                              * Larger values increase compression and decompression speed, but decrease ratio.
                              * Must be clamped between ZSTD_MINMATCH_MIN and ZSTD_MINMATCH_MAX.
                              * Note that currently, for all strategies < btopt, effective minimum is 4.
                              *                    , for all strategies > fast, effective maximum is 6.
                              * Special: value 0 means "use default minMatchLength". */
        ZSTD_c_targetLength = 106, /* Impact of this field depends on strategy.
                              * For strategies btopt, btultra & btultra2:
                              *     Length of Match considered "good enough" to stop search.
                              *     Larger values make compression stronger, and slower.
                              * For strategy fast:
                              *     Distance between match sampling.
                              *     Larger values make compression faster, and weaker.
                              * Special: value 0 means "use default targetLength". */
        ZSTD_c_strategy = 107,     /* See ZSTD_strategy enum definition.
                              * The higher the value of selected strategy, the more complex it is,
                              * resulting in stronger and slower compression.
                              * Special: value 0 means "use default strategy". */
        /* LDM mode parameters */
        ZSTD_c_enableLongDistanceMatching = 160, /* Enable long distance matching.
                                     * This parameter is designed to improve compression ratio
                                     * for large inputs, by finding large matches at long distance.
                                     * It increases memory usage and window size.
                                     * Note: enabling this parameter increases default ZSTD_c_windowLog to 128 MB
                                     * except when expressly set to a different value.
                                     * Note: will be enabled by default if ZSTD_c_windowLog >= 128 MB and
                                     * compression strategy >= ZSTD_btopt (== compression level 16+) */
        ZSTD_c_ldmHashLog = 161,   /* Size of the table for long distance matching, as a power of 2.
                              * Larger values increase memory usage and compression ratio,
                              * but decrease compression speed.
                              * Must be clamped between ZSTD_HASHLOG_MIN and ZSTD_HASHLOG_MAX
                              * default: windowlog - 7.
                              * Special: value 0 means "automatically determine hashlog". */
        ZSTD_c_ldmMinMatch = 162,  /* Minimum match size for long distance matcher.
                              * Larger/too small values usually decrease compression ratio.
                              * Must be clamped between ZSTD_LDM_MINMATCH_MIN and ZSTD_LDM_MINMATCH_MAX.
                              * Special: value 0 means "use default value" (default: 64). */
        ZSTD_c_ldmBucketSizeLog = 163, /* Log size of each bucket in the LDM hash table for collision resolution.
                              * Larger values improve collision resolution but decrease compression speed.
                              * The maximum value is ZSTD_LDM_BUCKETSIZELOG_MAX.
                              * Special: value 0 means "use default value" (default: 3). */
        ZSTD_c_ldmHashRateLog = 164, /* Frequency of inserting/looking up entries into the LDM hash table.
                              * Must be clamped between 0 and (ZSTD_WINDOWLOG_MAX - ZSTD_HASHLOG_MIN).
                              * Default is MAX(0, (windowLog - ldmHashLog)), optimizing hash table usage.
                              * Larger values improve compression speed.
                              * Deviating far from default value will likely result in a compression ratio decrease.
                              * Special: value 0 means "automatically determine hashRateLog". */

        /* frame parameters */
        ZSTD_c_contentSizeFlag = 200, /* Content size will be written into frame header _whenever known_ (default:1)
                              * Content size must be known at the beginning of compression.
                              * This is automatically the case when using ZSTD_compress2(),
                              * For streaming scenarios, content size must be provided with ZSTD_CCtx_setPledgedSrcSize() */
        ZSTD_c_checksumFlag = 201, /* A 32-bits checksum of content is written at end of frame (default:0) */
        ZSTD_c_dictIDFlag = 202,   /* When applicable, dictionary's ID is written into frame header (default:1) */

        /* multi-threading parameters */
        /* These parameters are only active if multi-threading is enabled (compiled with build macro ZSTD_MULTITHREAD).
         * Otherwise, trying to set any other value than default (0) will be a no-op and return an error.
         * In a situation where it's unknown if the linked library supports multi-threading or not,
         * setting ZSTD_c_nbWorkers to any value >= 1 and consulting the return value provides a quick way to check this property.
         */
        ZSTD_c_nbWorkers = 400,    /* Select how many threads will be spawned to compress in parallel.
                              * When nbWorkers >= 1, triggers asynchronous mode when invoking ZSTD_compressStream*() :
                              * ZSTD_compressStream*() consumes input and flush output if possible, but immediately gives back control to caller,
                              * while compression is performed in parallel, within worker thread(s).
                              * (note : a strong exception to this rule is when first invocation of ZSTD_compressStream2() sets ZSTD_e_end :
                              *  in which case, ZSTD_compressStream2() delegates to ZSTD_compress2(), which is always a blocking call).
                              * More workers improve speed, but also increase memory usage.
                              * Default value is `0`, aka "single-threaded mode" : no worker is spawned,
                              * compression is performed inside Caller's thread, and all invocations are blocking */
        ZSTD_c_jobSize = 401,      /* Size of a compression job. This value is enforced only when nbWorkers >= 1.
                              * Each compression job is completed in parallel, so this value can indirectly impact the nb of active threads.
                              * 0 means default, which is dynamically determined based on compression parameters.
                              * Job size must be a minimum of overlap size, or ZSTDMT_JOBSIZE_MIN (= 512 KB), whichever is largest.
                              * The minimum size is automatically and transparently enforced. */
        ZSTD_c_overlapLog = 402,   /* Control the overlap size, as a fraction of window size.
                              * The overlap size is an amount of data reloaded from previous job at the beginning of a new job.
                              * It helps preserve compression ratio, while each job is compressed in parallel.
                              * This value is enforced only when nbWorkers >= 1.
                              * Larger values increase compression ratio, but decrease speed.
                              * Possible values range from 0 to 9 :
                              * - 0 means "default" : value will be determined by the library, depending on strategy
                              * - 1 means "no overlap"
                              * - 9 means "full overlap", using a full window size.
                              * Each intermediate rank increases/decreases load size by a factor 2 :
                              * 9: full window;  8: w/2;  7: w/4;  6: w/8;  5:w/16;  4: w/32;  3:w/64;  2:w/128;  1:no overlap;  0:default
                              * default value varies between 6 and 9, depending on strategy */

        /* note : additional experimental parameters are also available
         * within the experimental section of the API.
         * At the time of this writing, they include :
         * ZSTD_c_rsyncable
         * ZSTD_c_format
         * ZSTD_c_forceMaxWindow
         * ZSTD_c_forceAttachDict
         * ZSTD_c_literalCompressionMode
         * ZSTD_c_targetCBlockSize
         * ZSTD_c_srcSizeHint
         * ZSTD_c_enableDedicatedDictSearch
         * ZSTD_c_stableInBuffer
         * ZSTD_c_stableOutBuffer
         * ZSTD_c_blockDelimiters
         * ZSTD_c_validateSequences
         * ZSTD_c_useBlockSplitter
         * ZSTD_c_useRowMatchFinder
         * Because they are not stable, it's necessary to define ZSTD_STATIC_LINKING_ONLY to access them.
         * note : never ever use experimentalParam? names directly;
         *        also, the enums values themselves are unstable and can still change.
         */
        ZSTD_c_experimentalParam1 = 500,
        ZSTD_c_experimentalParam2 = 10,
        ZSTD_c_experimentalParam3 = 1000,
        ZSTD_c_experimentalParam4 = 1001,
        ZSTD_c_experimentalParam5 = 1002,
        ZSTD_c_experimentalParam6 = 1003,
        ZSTD_c_experimentalParam7 = 1004,
        ZSTD_c_experimentalParam8 = 1005,
        ZSTD_c_experimentalParam9 = 1006,
        ZSTD_c_experimentalParam10 = 1007,
        ZSTD_c_experimentalParam11 = 1008,
        ZSTD_c_experimentalParam12 = 1009,
        ZSTD_c_experimentalParam13 = 1010,
        ZSTD_c_experimentalParam14 = 1011,
        ZSTD_c_experimentalParam15 = 1012
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ZSTD_bounds
    {
        public size_t error;
        public int lowerBound;
        public int upperBound;
    }

    // TODO:getBOunds, setParmeter, etc...


    #endregion

    #region Advanced decompression API

    // TODO:...

    #endregion

    #region Streaming

    [StructLayout(LayoutKind.Sequential)]
    public struct ZSTD_inBuffer
    {
        /// <summary>start of input buffer</summary>
        public void* src;
        /// <summary>size of input buffer</summary>
        public size_t size;
        /// <summary>position where reading stopped. Will be updated. Necessarily 0 &lt;= pos &lt;= size</summary>
        public size_t pos;
    }


    [StructLayout(LayoutKind.Sequential)]
    public struct ZSTD_outBuffer
    {
        /// <summary>start of output buffer</summary>
        public void* src;
        /// <summary>size of output buffer</summary>
        public size_t size;
        /// <summary>position where writing stopped. Will be updated. Necessarily 0 &lt;= pos &lt;= size</summary>
        public size_t pos;
    }

    #endregion

    #region Streaming Compression

    /// <summary>
    /// A ZSTD_CStream object is required to track streaming operation.
    /// Use ZSTD_createCStream() and ZSTD_freeCStream() to create/release resources.
    /// ZSTD_CStream objects can be reused multiple times on consecutive compression operations.
    /// It is recommended to re-use ZSTD_CStream since it will play nicer with system's memory, by re-using already allocated memory.
    /// For parallel execution, use one separate ZSTD_CStream per thread.
    /// </summary>
    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void* ZSTD_createCStream();

    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern size_t ZSTD_freeCStream(void* zcs);

    public enum ZSTD_EndDirective
    {
        ZSTD_e_continue = 0, /* collect more data, encoder decides when to output compressed result, for optimal compression ratio */
        ZSTD_e_flush = 1,    /* flush any data provided so far,
                        * it creates (at least) one new block, that can be decoded immediately on reception;
                        * frame will continue: any future data can still reference previously compressed data, improving compression.
                        * note : multithreaded compression will block to flush as much output as possible. */
        ZSTD_e_end = 2       /* flush any remaining data _and_ close current frame.
                        * note that frame is only closed after compressed data is fully flushed (return value == 0).
                        * After that point, any additional data starts a new frame.
                        * note : each frame is independent (does not reference any content from previous frame).
                        : note : multithreaded compression will block to flush as much output as possible. */
    }


    [DllImport(LibZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern size_t ZSTD_compressStream2(void* cctx, ref ZSTD_outBuffer output, ref ZSTD_inBuffer input, ZSTD_EndDirective endOp);

    #endregion
}