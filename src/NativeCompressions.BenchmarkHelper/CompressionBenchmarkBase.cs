using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using NativeCompressions.LZ4;
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

public abstract class Lz4BenchmarkBase : CompressionBenchmarkBase<int>
{
    public override IEnumerable<int> GetCompressionLevels() => CompressionLevels;

    // TODO: remove it.
    public IEnumerable<int> CompressionLevels => Enumerable.Sequence(
        start: NativeCompressions.LZ4.LZ4.MinCompressionLevel,
        endInclusive: NativeCompressions.LZ4.LZ4.MaxCompressionLevel,
        step: 1);

    public virtual LZ4FrameOptions LZ4FrameOptions => LZ4FrameOptions.Default;
    public virtual LZ4CompressionDictionary? LZ4CompressionDictionary => null;

    protected override int GetMaxCompressedLength(int inputSize, int compressionLevel)
    {
        return LZ4.LZ4.GetMaxCompressedLength(inputSize, LZ4FrameOptions with { CompressionLevel = compressionLevel });
    }

    protected override int CompressCore(byte[] source, byte[] destination, int compressionLevel)
    {
        return NativeCompressions.LZ4.LZ4.Compress(source, destination, LZ4FrameOptions.Default with { CompressionLevel = compressionLevel }, LZ4CompressionDictionary);
    }

    protected override int DecompressCore(byte[] source, byte[] destination)
    {
        return NativeCompressions.LZ4.LZ4.Decompress(source, destination, LZ4CompressionDictionary);
    }
}
