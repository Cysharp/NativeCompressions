using NativeCompressions.BenchmarkHelper;

namespace Benchmark;

public class Silesia_Lz4 : Lz4BenchmarkBase
{
    protected override byte[] GetTargetSource()
    {
        return Resources.Silesia;
    }
}

public class Silesia_Zstandard : ZstandardBenchmarkBase
{
    protected override byte[] GetTargetSource()
    {
        return Resources.Silesia;
    }
}

public class Silesia_Brotli : BrotliBenchmarkBase
{
    protected override byte[] GetTargetSource()
    {
        return Resources.Silesia;
    }
}

public class Silesia_GZip : GZipBenchmarkBase
{
    protected override byte[] GetTargetSource()
    {
        return Resources.Silesia;
    }
}

public class SilesiaMultiThread_Lz4 : CompressionBenchmarkBase<int>
{
    public override IEnumerable<int> GetLevels() => Enumerable.Sequence(
        start: 1,
        endInclusive: 6, // Environment.ProcessorCount,
        step: 1);

    protected override byte[] GetTargetSource()
    {
        return Resources.Silesia;
    }

    protected override int GetMaxCompressedLength(int inputSize, int _)
    {
        return NativeCompressions.LZ4.GetMaxCompressedLength(inputSize);
    }

    protected override int CompressCore(byte[] source, byte[] destination, int maxDegreeOfParallelism)
    {
        var writer = new ArrayPipeWriter(destination);
        NativeCompressions.LZ4.CompressAsync(source, writer, maxDegreeOfParallelism: maxDegreeOfParallelism).GetAwaiter().GetResult();
        return writer.WrittenCount;
    }

    protected override int DecompressCore(byte[] source, byte[] destination)
    {
        var writer = new ArrayPipeWriter(destination);
        NativeCompressions.LZ4.DecompressAsync(source, writer).GetAwaiter().GetResult();
        return writer.WrittenCount;
    }
}
