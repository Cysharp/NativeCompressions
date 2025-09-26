using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using System.IO.Compression;

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
    protected abstract byte[] GetTargetSource();

    [ParamsSource(nameof(GetCompressionLevels))]
    public TCompressionLevel Level { get; set; } = default!;

    byte[] source = default!;
    byte[] destination = default!;

    byte[] compressedData = default!;
    byte[] decompressDestination = default!;

    [GlobalSetup]
    public void Init()
    {
        source = GetTargetSource();
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

            foreach (var level in GetCompressionLevels())
            {
                var source = GetTargetSource();
                var destination = new byte[GetMaxCompressedLength(source.Length, level)];
                var written = CompressCore(source, destination, level);

                payloadDictionary[(type, level!)] = new PayloadData(source, destination.AsSpan(0, (int)written).ToArray());
            }

            return payloadDictionary.TryGetValue((type, parameterLevel), out payloadData)
                ? payloadData
                : throw new InvalidOperationException($"Payload data not found for {type} with parameter {parameterLevel}");
        }
    }
}

public abstract class Lz4BenchmarkBase : CompressionBenchmarkBase<int>
{
    public override IEnumerable<int> GetCompressionLevels() => Enumerable.Sequence(
        start: LZ4.MinCompressionLevel,
        endInclusive: LZ4.MaxCompressionLevel,
        step: 1);

    public virtual LZ4FrameOptions LZ4FrameOptions => LZ4FrameOptions.Default;
    public virtual LZ4CompressionDictionary? LZ4CompressionDictionary => null;

    protected override int GetMaxCompressedLength(int inputSize, int compressionLevel)
    {
        return LZ4.GetMaxCompressedLength(inputSize, LZ4FrameOptions with { CompressionLevel = compressionLevel });
    }

    protected override int CompressCore(byte[] source, byte[] destination, int compressionLevel)
    {
        return LZ4.Compress(source, destination, LZ4FrameOptions.Default with { CompressionLevel = compressionLevel }, LZ4CompressionDictionary);
    }

    protected override int DecompressCore(byte[] source, byte[] destination)
    {
        return LZ4.Decompress(source, destination, LZ4CompressionDictionary);
    }
}

public abstract class ZstandardBenchmarkBase : CompressionBenchmarkBase<int>
{
    public override IEnumerable<int> GetCompressionLevels() => Enumerable.Sequence(
        start: -4, // Zstandard's min compression level is -131072 so use -4 instead.
        endInclusive: Zstandard.MaxCompressionLevel,
        step: 1);

    public virtual ZstandardCompressionOptions ZstandardCompressionOptions => ZstandardCompressionOptions.Default;
    public virtual ZstandardCompressionDictionary? ZstandardCompressionDictionary => null;

    protected override int GetMaxCompressedLength(int inputSize, int compressionLevel)
    {
        return Zstandard.GetMaxCompressedLength(inputSize);
    }

    protected override int CompressCore(byte[] source, byte[] destination, int compressionLevel)
    {
        var options = ZstandardCompressionOptions;
        if (ZstandardCompressionDictionary == null && options.IsDefault) // TODO: this optimize should handle in Zstandard.Compress
        {
            return Zstandard.Compress(source, destination, compressionLevel);
        }

        return Zstandard.Compress(source, destination, options with { CompressionLevel = compressionLevel }, ZstandardCompressionDictionary);
    }

    protected override int DecompressCore(byte[] source, byte[] destination)
    {
        return Zstandard.Decompress(source, destination, ZstandardCompressionDictionary);
    }
}

public abstract class BrotliBenchmarkBase : CompressionBenchmarkBase<int>
{
    //internal static partial class BrotliUtils
    //  public const int Quality_Min = 0;
    //  public const int Quality_Default = 4;
    //  public const int Quality_Max = 11;
    //  public const int WindowBits_Min = 10;
    //  public const int WindowBits_Default = 22;
    //  public const int WindowBits_Max = 24;
    public override IEnumerable<int> GetCompressionLevels() => Enumerable.Sequence(
        start: 0,
        endInclusive: 11,
        step: 1);

    protected override int GetMaxCompressedLength(int inputSize, int compressionLevel)
    {
        return Zstandard.GetMaxCompressedLength(inputSize);
    }

    protected override int CompressCore(byte[] source, byte[] destination, int compressionLevel)
    {
        BrotliEncoder.TryCompress(source, destination, out var bytesWritten, quality: compressionLevel, window: 22);
        return bytesWritten;
    }

    protected override int DecompressCore(byte[] source, byte[] destination)
    {
        BrotliDecoder.TryDecompress(source, destination, out var bytesWritten);
        return bytesWritten;
    }
}

public abstract class GZipBenchmarkBase : CompressionBenchmarkBase<CompressionLevel>
{
    public override IEnumerable<CompressionLevel> GetCompressionLevels() => [
        CompressionLevel.Fastest,
        CompressionLevel.Optimal,
#if NET6_0_OR_GREATER
        CompressionLevel.SmallestSize
#endif
    ];

    protected override int GetMaxCompressedLength(int inputSize, CompressionLevel compressionLevel)
    {
        long overhead = 18;
        long blockOverhead = (inputSize / 65535) * 5;
        if (inputSize % 65535 != 0) blockOverhead += 5;
        return checked((int)(inputSize + overhead + blockOverhead));
    }

    protected override int CompressCore(byte[] source, byte[] destination, CompressionLevel compressionLevel)
    {
        using var ms = new MemoryStream(destination, writable: true);
        using var gzip = new GZipStream(ms, compressionLevel, leaveOpen: true);

        gzip.Write(source, 0, source.Length);
        gzip.Flush();
        gzip.Close();

        return (int)ms.Position;
    }

    protected override int DecompressCore(byte[] source, byte[] destination)
    {
        using var ms = new MemoryStream(source);
        using var gzip = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true);
        using var destMs = new MemoryStream(destination, writable: true);

        gzip.CopyTo(destMs);
        return (int)destMs.Position;
    }
}
