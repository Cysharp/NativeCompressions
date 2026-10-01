using Benchmark.BenchmarkNetUtilities;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using NativeCompressions;
using Orleans.Serialization.Buffers;
using System.ComponentModel;
using System.IO.Compression;
using System.Threading.Tasks;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Benchmark;


[PayloadColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[HideColumns(Column.Error)]
public class AllCompressDecompress
{
    byte[] src = default!;
    public byte[] dest = default!;
    public ArrayBufferPipeWriter writer;


    byte[] compressed1;
    byte[] compressed2;
    byte[] compressed3;
    byte[] compressed4;
    byte[] compressed5;
    // byte[] compressed6;
    byte[] compressed7;
    byte[] compressed8;
    byte[] compressed9;

    public AllCompressDecompress()
    {
        src = Resources.Silesia;
        var maxSize = NativeCompressions.Zstandard.GetMaxCompressedLength(src.Length);
        dest = new byte[maxSize];
        writer = new ArrayBufferPipeWriter(maxSize);

        var i = K4os_LZ4_Encode();
        compressed1 = dest.AsSpan(0, i).ToArray();

        i = K4os_LZ4_FrameEncode();
        compressed2 = dest.AsSpan(0, i).ToArray();

        i = NativeCompressions_LZ4_Compress();
        compressed3 = dest.AsSpan(0, i).ToArray();

        i = NativeCompressions_LZ4_Block_Compress();
        compressed4 = dest.AsSpan(0, i).ToArray();

        i = NativeCompressions_Zstandard_Compress_Default();
        compressed5 = dest.AsSpan(0, i).ToArray();

        i = NativeCompressions_Zstandard_Compress_Minus4();
        compressed7 = dest.AsSpan(0, i).ToArray();

        i = BrotliEncoder_TryCompress();
        compressed8 = dest.AsSpan(0, i).ToArray();

        i = GZipStream_Optimal_Compress();
        compressed9 = dest.AsSpan(0, i).ToArray();
    }

    //[Benchmark]
    //public int ZstdSharp_Zstandard_Compress_Default()
    //{
    //    using var compressor = new Compressor();
    //    return compressor.Wrap((ReadOnlySpan<byte>)src, (Span<byte>)dest);
    //}

    //[Benchmark]
    //public int ZstdSharp_Zstandard_Compress_Minus4()
    //{
    //    using var compressor = new Compressor(-4);
    //    return compressor.Wrap((ReadOnlySpan<byte>)src, (Span<byte>)dest);
    //}

    //[Benchmark]
    //public int ZstdSharp_Zstandard_Compress_Multithread()
    //{
    //    using var compressor = new Compressor();
    //    compressor.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers, 4);
    //    return compressor.Wrap((ReadOnlySpan<byte>)src, (Span<byte>)dest);
    //}


    [Benchmark(Description = "K4os LZ4 Encode(Block)")]
    [BenchmarkCategory("Compress")]
    public int K4os_LZ4_Encode()
    {
        return K4os.Compression.LZ4.LZ4Codec.Encode(src, dest, K4os.Compression.LZ4.LZ4Level.L00_FAST);
    }

    [Benchmark(Description = "K4os LZ4 Encode(Frame)")]
    [BenchmarkCategory("Compress")]
    public int K4os_LZ4_FrameEncode()
    {
        return K4os.Compression.LZ4.Streams.LZ4Frame.Encode(src, dest, K4os.Compression.LZ4.LZ4Level.L00_FAST);
    }

    [Benchmark(Description = "NativeCompressions LZ4 Compress(Frame)")]
    [BenchmarkCategory("Compress")]
    public int NativeCompressions_LZ4_Compress()
    {
        return NativeCompressions.LZ4.Compress(src, dest);
    }

    [Benchmark(Description = "NativeCompressions LZ4 Compress(Block)")]
    [BenchmarkCategory("Compress")]
    public int NativeCompressions_LZ4_Block_Compress()
    {
        return NativeCompressions.LZ4.Block.Compress(src, dest);
    }

    [Benchmark(Description = "NativeCompressions Zstandard Compress(Default)")]
    [BenchmarkCategory("Compress")]
    public int NativeCompressions_Zstandard_Compress_Default()
    {
        return NativeCompressions.Zstandard.Compress(src, dest, ZstandardCompressionOptions.Default);
    }

    [Benchmark(Description = "NativeCompressions Zstandard Compress(Level: -4)")]
    [BenchmarkCategory("Compress")]
    public int NativeCompressions_Zstandard_Compress_Minus4()
    {
        return NativeCompressions.Zstandard.Compress(src, dest, ZstandardCompressionOptions.Default with { CompressionLevel = -4 });
    }

    [Benchmark(Description = "BrotliEncoder TryCompress")]
    [BenchmarkCategory("Compress")]
    public int BrotliEncoder_TryCompress()
    {
        BrotliEncoder.TryCompress(src, dest, out var bytesWritten);
        return bytesWritten;
    }

    [Benchmark(Description = "GZipStream Compress(Level: Optimal)")]
    [BenchmarkCategory("Compress")]
    public int GZipStream_Optimal_Compress()
    {
        using var srcStream = new MemoryStream(src);
        using var ms = new MemoryStream(dest);
        using var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true);

        srcStream.CopyTo(gzip);
        gzip.Close();

        return (int)ms.Position;
    }

    // decompress

    [Benchmark(Description = "K4os LZ4 Decode(Block)")]
    [BenchmarkCategory("Decompress")]
    public int K4os_LZ4_Decode()
    {
        return K4os.Compression.LZ4.LZ4Codec.Decode(compressed1, dest);
    }

    [Benchmark(Description = "K4os LZ4 Decode(Frame)")]
    [BenchmarkCategory("Decompress")]
    public int K4os_LZ4_FrameDecode()
    {
        var reader = K4os.Compression.LZ4.Streams.LZ4Frame.Decode(compressed2);
        return reader.ReadManyBytes(dest);
    }

    [Benchmark(Description = "NativeCompressions LZ4 Decompress(Frame)")]
    [BenchmarkCategory("Decompress")]
    public int NativeCompressions_LZ4_Decompress()
    {
        return NativeCompressions.LZ4.Decompress(compressed3, dest);
    }

    [Benchmark(Description = "NativeCompressions LZ4 Decompress(Block)")]
    [BenchmarkCategory("Decompress")]
    public int NativeCompressions_LZ4_Block_Decompress()
    {
        return NativeCompressions.LZ4.Block.Decompress(compressed4, dest);
    }

    [Benchmark(Description = "NativeCompressions Zstandard Decompress(Default)")]
    [BenchmarkCategory("Decompress")]
    public int NativeCompressions_Zstandard_Decompress_Default()
    {
        return NativeCompressions.Zstandard.Decompress(compressed5, dest);
    }

    [Benchmark(Description = "NativeCompressions Zstandard Decompress(Level: -4)")]
    [BenchmarkCategory("Decompress")]
    public int NativeCompressions_Zstandard_Decompress_Minus4()
    {
        return NativeCompressions.Zstandard.Decompress(compressed7, dest);
    }

    [Benchmark(Description = "BrotliDecoder TryDecompress")]
    [BenchmarkCategory("Decompress")]
    public int BrotliDecoder_TryDecompress()
    {
        BrotliDecoder.TryDecompress(compressed8, dest, out var bytesWritten);
        return bytesWritten;
    }


    [Benchmark(Description = "GZipStream Decompress(Level: Optimal)")]
    [BenchmarkCategory("Decompress")]
    public int GZipStream_Optimal_Decompress()
    {
        using var srcStream = new MemoryStream(compressed9);
        using var gzip = new GZipStream(srcStream, CompressionMode.Decompress, leaveOpen: true);
        using var ms = new MemoryStream(dest);

        gzip.CopyTo(ms);
        gzip.Close();

        return (int)ms.Position;
    }
}

//[PayloadColumn]
//public class ZstandardSimpleDecode
//{
//    byte[] srcNativeCompressions = default!;
//    byte[] srcZstdSharp = default!;

//    public byte[] dest = default!;

//    public ZstandardSimpleDecode()
//    {
//        dest = new byte[Resources.Silesia.Length];

//        var enc = new ZstandardSimpleEncode();

//        var written = enc.NativeCompressions_Zstandard_Compress_Default();
//        srcNativeCompressions = enc.dest.AsSpan(0, written).ToArray();

//        written = enc.ZstdSharp_Zstandard_Compress_Default();
//        srcZstdSharp = enc.dest.AsSpan(0, written).ToArray();
//    }

//    [Benchmark]
//    public int ZstdSharp_Zstandard_Decode()
//    {
//        using var decompressor = new Decompressor();
//        return decompressor.Unwrap((ReadOnlySpan<byte>)srcZstdSharp, (Span<byte>)dest);
//    }

//    [Benchmark]
//    public int NativeCompressions_Zstandard_Decompress()
//    {
//        return NativeCompressions.Zstandard.Zstandard.Decompress(srcNativeCompressions, dest);
//    }
//}
