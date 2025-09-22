using NativeCompressions.LZ4;
using NativeCompressions.BenchmarkHelper;

namespace Benchmark;

public class Silesia_Lz4 : Lz4BenchmarkBase
{
    protected override byte[] GetTargetSource(int compressionLevel)
    {
        return Resources.Silesia;
    }
}

//public class Silesia_Lz4 : CompressionBenchmarkBase<int>
//{
//    public override IEnumerable<int> GetCompressionLevels() => CompressionLevels;

//    // TODO: remove it.
//    public IEnumerable<int> CompressionLevels => Enumerable.Sequence(
//        start: NativeCompressions.LZ4.LZ4.MinCompressionLevel,
//        endInclusive: NativeCompressions.LZ4.LZ4.MaxCompressionLevel,
//        step: 1);

//    protected override int GetMaxCompressedLength(int inputSize, int compressionLevel)
//    {
//        return NativeCompressions.LZ4.LZ4.GetMaxCompressedLength(inputSize, LZ4FrameOptions.Default with { CompressionLevel = compressionLevel });
//    }

//    protected override byte[] GetTargetSource(int compressionLevel)
//    {
//        return Resources.Silesia;
//    }

//    protected override int CompressCore(byte[] source, byte[] destination, int compressionLevel)
//    {
//        return NativeCompressions.LZ4.LZ4.Compress(source, destination, LZ4FrameOptions.Default with { CompressionLevel = compressionLevel });
//    }

//    protected override int DecompressCore(byte[] source, byte[] destination)
//    {
//        return NativeCompressions.LZ4.LZ4.Decompress(source, destination);
//    }
//}
