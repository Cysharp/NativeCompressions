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
public abstract class CompressionBenchmarkBase<TLevel> // level is mainly CompressionLevel but can use for window-size, etc...
{
    // v0.15.4 allows abstract method/property https://github.com/dotnet/BenchmarkDotNet/pull/2832

    public abstract IEnumerable<TLevel> GetLevels();

    protected abstract int GetMaxCompressedLength(int inputSize, TLevel level);
    protected abstract byte[] GetTargetSource();

    [ParamsSource(nameof(GetLevels))]
    public TLevel Level { get; set; } = default!;

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

    protected abstract int CompressCore(byte[] source, byte[] destination, TLevel level);
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

            foreach (var level in GetLevels())
            {
                var source = GetTargetSource();
                var destination = new byte[GetMaxCompressedLength(source.Length, level)];
                var written = CompressCore(source, destination, level);

                payloadDictionary[(type, level!)] = new PayloadData(source.Length, written);
            }

            return payloadDictionary.TryGetValue((type, parameterLevel), out payloadData)
                ? payloadData
                : throw new InvalidOperationException($"Payload data not found for {type} with parameter {parameterLevel}");
        }
    }

    internal void CleanupPayloadData()
    {
    }
}

public abstract class Lz4BenchmarkBase : CompressionBenchmarkBase<int>
{
    public override IEnumerable<int> GetLevels() => Enumerable.Sequence(
        start: LZ4.MinCompressionLevel,
        endInclusive: LZ4.MaxCompressionLevel,
        step: 1);

    public virtual LZ4CompressionOptions CompressionOptions => LZ4CompressionOptions.Default;
    public virtual LZ4DecompressionOptions DecompressionOptions => LZ4DecompressionOptions.Default;

    protected override int GetMaxCompressedLength(int inputSize, int compressionLevel)
    {
        return LZ4.GetMaxCompressedLength(inputSize, CompressionOptions with { CompressionLevel = compressionLevel });
    }

    protected override int CompressCore(byte[] source, byte[] destination, int compressionLevel)
    {
        return LZ4.Compress(source, destination, LZ4CompressionOptions.Default with { CompressionLevel = compressionLevel });
    }

    protected override int DecompressCore(byte[] source, byte[] destination)
    {
        return LZ4.Decompress(source, destination, DecompressionOptions);
    }
}

public abstract class ZstandardBenchmarkBase : CompressionBenchmarkBase<int>
{
    public override IEnumerable<int> GetLevels() => [
        ..new int[] { -4, -3, -2, -1 }, // Zstandard's min compression level is -131072 so use -4 instead.
        ..Enumerable.Sequence(
            start: 1, // // 0 means default compression level so ignore use 0
            endInclusive: Zstandard.MaxCompressionLevel, // 22
            step: 1)];

    public virtual ZstandardCompressionOptions CompressionOptions => ZstandardCompressionOptions.Default;
    public virtual ZstandardDecompressionOptions DecompressionOptions => ZstandardDecompressionOptions.Default;

    protected override int GetMaxCompressedLength(int inputSize, int compressionLevel)
    {
        return Zstandard.GetMaxCompressedLength(inputSize);
    }

    protected override int CompressCore(byte[] source, byte[] destination, int compressionLevel)
    {
        if (CompressionOptions.IsDefault)
        {
            return Zstandard.Compress(source, destination, compressionLevel);
        }

        return Zstandard.Compress(source, destination, CompressionOptions with { CompressionLevel = compressionLevel });
    }

    protected override int DecompressCore(byte[] source, byte[] destination)
    {
        return Zstandard.Decompress(source, destination, DecompressionOptions);
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
    public override IEnumerable<int> GetLevels() => Enumerable.Sequence(
        start: 0,
        endInclusive: 11,
        step: 1);

    public virtual int CompressionWindow => 22; // WindowBits_Default

    protected override int GetMaxCompressedLength(int inputSize, int quality)
    {
        return Zstandard.GetMaxCompressedLength(inputSize);
    }

    protected override int CompressCore(byte[] source, byte[] destination, int quality)
    {
        BrotliEncoder.TryCompress(source, destination, out var bytesWritten, quality: quality, window: CompressionWindow);
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
    public override IEnumerable<CompressionLevel> GetLevels() => [
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
