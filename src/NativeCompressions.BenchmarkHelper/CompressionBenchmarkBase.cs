using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using System.Collections.Concurrent;

namespace NativeCompressions.BenchmarkHelper;

[PayloadSizeColumn]
[CompressionRatioColumn]
[CompressionThroughputColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[HideColumns(Column.Error)]
public abstract class CompressionBenchmarkBase<TCompressionLevel>
{
    // v0.15.4 allows abstract method/property https://github.com/dotnet/BenchmarkDotNet/pull/2832

    public abstract IEnumerable<TCompressionLevel> GetCompressionLevels();

    protected abstract int GetMaxCompressedLength(int inputSize, TCompressionLevel compressionLevel);
    protected abstract byte[] GetTargetSource(TCompressionLevel compressionLevel);

    [ParamsSource("CompressionLevels")] // TODO: modify to GetCompressionLevels after v0.15.4 released
    public TCompressionLevel Level { get; set; } = default!;

    byte[] source = default!;
    byte[] destination = default!;

    byte[] compressedData = default!;
    byte[] decompressDestination = default!;

    [GlobalSetup]
    public void Init()
    {
        source = GetTargetSource(Level);
        destination = new byte[GetMaxCompressedLength(source.Length, Level)];

        var written = Compress(); // call compress in init.
        compressedData = destination.AsSpan(0, (int)written).ToArray();
        decompressDestination = new byte[source.Length];
    }

    [Benchmark]
    [BenchmarkCategory("Compress")]
    public int Compress()
    {
        return CompressCore(source, destination, Level);
    }

    [Benchmark]
    [BenchmarkCategory("Decompress")]
    public int Decompress()
    {
        return DecompressCore(compressedData, decompressDestination);
    }

    protected abstract int CompressCore(byte[] source, byte[] destination, TCompressionLevel compressionLevel);
    protected abstract int DecompressCore(byte[] source, byte[] destination);

    // for PayloadColumn, need to generate data for summary
    static ConcurrentDictionary<(Type, object), PayloadData>? payloadDictionary;

    // this method called from reflection so all fields are not initialized.
    internal PayloadData GetPayloadData(object parameterLevel)
    {
        var type = this.GetType();

        if (payloadDictionary == null) // setup only once
        {
            payloadDictionary = new();

            foreach (var level in GetCompressionLevels())
            {
                var source = GetTargetSource(level);
                var destination = new byte[GetMaxCompressedLength(source.Length, level)];
                var written = CompressCore(source, destination, level);

                payloadDictionary[(type, level!)] = new PayloadData(source, destination.AsSpan(0, (int)written).ToArray());
            }
        }

        return payloadDictionary[(type, parameterLevel)];
    }
}
