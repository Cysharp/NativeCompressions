using NativeCompressions.LZ4;
using NativeCompressions.BenchmarkHelper;

namespace Benchmark;

public class Lz4Simple2 : CompressionBenchmarkBase<int>
{
    // public TCompressionLevel[] CompressionLevels =>
    public int[] CompressionLevels => [1, 2];

    /*
    Enumerable.Sequence(
        //start: NativeCompressions.LZ4.LZ4.MinCompressionLevel,
        //endInclusive: NativeCompressions.LZ4.LZ4.MaxCompressionLevel,
        start:1,
        endInclusive:2,
        step: 1).ToArray();*/

    protected override int GetMaxCompressedLength(int inputSize, int compressionLevel)
    {
        return NativeCompressions.LZ4.LZ4.GetMaxCompressedLength(inputSize, LZ4FrameOptions.Default with { CompressionLevel = compressionLevel });
    }

    protected override byte[] GetTargetSource(int compressionLevel)
    {
        return Resources.Silesia;
    }

    protected override long CompressCore(byte[] source, byte[] destination, int compressionLevel)
    {
        return NativeCompressions.LZ4.LZ4.Compress(source, destination, LZ4FrameOptions.Default with { CompressionLevel = compressionLevel });
    }

    protected override long DecompressCore(byte[] source, byte[] destination)
    {
        return NativeCompressions.LZ4.LZ4.Decompress(source, destination);
    }
}
