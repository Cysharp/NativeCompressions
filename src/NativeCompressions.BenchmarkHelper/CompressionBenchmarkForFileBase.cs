using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;

namespace NativeCompressions.BenchmarkHelper;

[PayloadSizeColumn]
[CompressionRatioColumn]
[CompressionThroughputColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[HideColumns(Column.Error)]
public abstract class CompressionBenchmarkForFileBase<TLevel>
{
    public abstract IEnumerable<TLevel> GetLevels();

    protected abstract string GetSourceFilePath();
    protected abstract string GetCompressDestinationFilePath();
    protected abstract string GetDecompressDestinationFilePath();

    [ParamsSource(nameof(GetLevels))]
    public TLevel Level { get; set; } = default!;

    string compressedDataFilePath = "";

    [GlobalSetup]
    public async Task InitAsync()
    {
        await SetupCoreAsync();

        var source = GetSourceFilePath();
        var destination = GetCompressDestinationFilePath();

        await CompressCoreAsync(source, destination, Level);

        compressedDataFilePath = destination;
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await CleanupCoreAsync();
    }

    public abstract ValueTask SetupCoreAsync();

    public abstract ValueTask CleanupCoreAsync();

    [Benchmark]
    [BenchmarkCategory("Compress")]
    public ValueTask CompressAsync()
    {
        return CompressCoreAsync(GetSourceFilePath(), GetCompressDestinationFilePath(), Level);
    }

    [Benchmark]
    [BenchmarkCategory("Decompress")]
    public ValueTask DecompressAsync()
    {
        return DecompressCoreAsync(compressedDataFilePath, GetDecompressDestinationFilePath());
    }

    protected abstract ValueTask CompressCoreAsync(string source, string destination, TLevel level);

    protected abstract ValueTask DecompressCoreAsync(string source, string destination);

    // for PayloadColumn, need to generate data for summary
    static readonly object gate = new object();
    static Dictionary<(Type, object), PayloadData>? payloadDictionary;

    // this method called from reflection so all fields are not initialized.
    internal PayloadData GetPayloadData(object parameterLevel)
    {
        var type = this.GetType();

        lock (gate)
        {
            if (payloadDictionary == null)
            {
                payloadDictionary = new();
            }

            if (payloadDictionary.TryGetValue((type, parameterLevel), out var payloadData))
            {
                return payloadData;
            }

            SetupCoreAsync().GetAwaiter().GetResult();
            try
            {
                foreach (var level in GetLevels())
                {
                    var source = GetSourceFilePath();
                    var destination = GetCompressDestinationFilePath();
                    CompressCoreAsync(source, destination, level).GetAwaiter().GetResult();

                    payloadDictionary[(type, level!)] = new PayloadData(new FileInfo(source).Length, new FileInfo(destination).Length);
                }
            }
            finally
            {
                CleanupCoreAsync().GetAwaiter().GetResult();
            }

            return payloadDictionary.TryGetValue((type, parameterLevel), out payloadData)
                ? payloadData
                : throw new InvalidOperationException($"Payload data not found for {type} with parameter {parameterLevel}");
        }
    }
}

public abstract class Lz4BenchmarkForFileBase : CompressionBenchmarkForFileBase<int>
{
    public override IEnumerable<int> GetLevels() => Enumerable.Sequence(
        start: LZ4.MinCompressionLevel,
        endInclusive: LZ4.MaxCompressionLevel,
        step: 1);

    public virtual LZ4CompressionOptions CompressionOptions => LZ4CompressionOptions.Default;
    public virtual LZ4DecompressionOptions DecompressionOptions => LZ4DecompressionOptions.Default;

    public virtual int? MaxDegreeOfParallelism => null;

    protected override ValueTask CompressCoreAsync(string source, string destination, int compressionLevel)
    {
        return LZ4.CompressAsync(source, destination, CompressionOptions, MaxDegreeOfParallelism);
    }

    protected override ValueTask DecompressCoreAsync(string source, string destination)
    {
        return LZ4.DecompressAsync(source, destination, DecompressionOptions);
    }
}
