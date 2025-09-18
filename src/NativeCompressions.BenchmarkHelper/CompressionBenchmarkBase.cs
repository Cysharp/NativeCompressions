using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;

namespace NativeCompressions.BenchmarkHelper;

// TODO: CompressionRatio
// [PayloadColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[HideColumns(Column.Error)]
public abstract class CompressionBenchmarkBase<TCompressionLevel>
{
    // public abstract TCompressionLevel[] GetCompressionLevels();
    protected abstract int GetMaxCompressedLength(int inputSize, TCompressionLevel compressionLevel);
    protected abstract byte[] GetTargetSource(TCompressionLevel compressionLevel);

    [ParamsSource("CompressionLevels")]
    public TCompressionLevel Level { get; set; } = default!;

    public long PayloadSize { get; private set; }

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

        PayloadSize = written;
    }

    [Benchmark]
    [BenchmarkCategory("Compress")]
    public long Compress()
    {
        return CompressCore(source, destination, Level);
    }

    [Benchmark]
    [BenchmarkCategory("Decompress")]
    public long Decompress()
    {
        return DecompressCore(compressedData, decompressDestination);
    }

    protected abstract long CompressCore(byte[] source, byte[] destination, TCompressionLevel compressionLevel);
    protected abstract long DecompressCore(byte[] source, byte[] destination);
}
