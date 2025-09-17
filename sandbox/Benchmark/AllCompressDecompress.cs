using Benchmark.BenchmarkNetUtilities;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using NativeCompressions.LZ4;
using NativeCompressions.Zstandard;
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
        var maxSize = NativeCompressions.Zstandard.Zstandard.GetMaxCompressedLength(src.Length);
        dest = new byte[maxSize];
        writer = new ArrayBufferPipeWriter(maxSize);

        var i = K4os_LZ4_Encode();
        compressed1 = dest.AsSpan(0, i).ToArray();

        i = K4os_LZ4_FrameEncode();
        compressed2 = dest.AsSpan(0, i).ToArray();

        i = NativeCompressions_LZ4_Compress();
        compressed3 = dest.AsSpan(0, i).ToArray();

        i = NativeCompressions_LZ4_CompressMultiThread().GetAwaiter().GetResult();
        compressed4 = dest.AsSpan(0, i).ToArray();

        i = NativeCompressions_Zstandard_Compress_Default();
        compressed5 = dest.AsSpan(0, i).ToArray();

        //i = NativeCompressions_Zstandard_Compress_Multithread();
        //compressed6 = dest.AsSpan(0, i).ToArray();

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


    [Benchmark]
    [BenchmarkCategory("Compress")]
    public int K4os_LZ4_Encode()
    {
        return K4os.Compression.LZ4.LZ4Codec.Encode(src, dest, K4os.Compression.LZ4.LZ4Level.L00_FAST);
    }

    [Benchmark]
    [BenchmarkCategory("Compress")]
    public int K4os_LZ4_FrameEncode()
    {
        return K4os.Compression.LZ4.Streams.LZ4Frame.Encode(src, dest, K4os.Compression.LZ4.LZ4Level.L00_FAST);
    }

    [Benchmark]
    [BenchmarkCategory("Compress")]
    public int NativeCompressions_LZ4_Compress()
    {
        return NativeCompressions.LZ4.LZ4.Compress(src, dest);
    }

    [Benchmark]
    [BenchmarkCategory("Compress")]
    public async Task<int> NativeCompressions_LZ4_CompressMultiThread()
    {
        writer.ResetWrittenCount();
        await NativeCompressions.LZ4.LZ4.CompressAsync(src, writer, LZ4FrameOptions.Default);
        return (int)writer.WrittenCount;
    }

    [Benchmark]
    [BenchmarkCategory("Compress")]
    public int NativeCompressions_Zstandard_Compress_Default()
    {
        return NativeCompressions.Zstandard.Zstandard.Compress(src, dest, ZstandardCompressionOptions.Default);
    }

    [Benchmark]
    [BenchmarkCategory("Compress")]
    public int NativeCompressions_Zstandard_Compress_Minus4()
    {
        return NativeCompressions.Zstandard.Zstandard.Compress(src, dest, ZstandardCompressionOptions.Default with { CompressionLevel = -4 });
    }

    [Benchmark]
    [BenchmarkCategory("Compress")]
    public int NativeCompressions_Zstandard_Compress_Multithread()
    {
        return NativeCompressions.Zstandard.Zstandard.Compress(src, dest, ZstandardCompressionOptions.Default with { NbWorkers = Environment.ProcessorCount });
    }

    [Benchmark]
    [BenchmarkCategory("Compress")]
    public int BrotliEncoder_TryCompress()
    {
        BrotliEncoder.TryCompress(src, dest, out var bytesWritten);
        return bytesWritten;
    }

    [Benchmark]
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

    [Benchmark]
    [BenchmarkCategory("Decompress")]
    public int K4os_LZ4_Decode()
    {
        return K4os.Compression.LZ4.LZ4Codec.Decode(compressed1, dest);
    }

    [Benchmark]
    [BenchmarkCategory("Decompress")]
    public int K4os_LZ4_FrameDecode()
    {
        var reader = K4os.Compression.LZ4.Streams.LZ4Frame.Decode(compressed2);
        return reader.ReadManyBytes(dest);
    }

    [Benchmark]
    [BenchmarkCategory("Decompress")]
    public int NativeCompressions_LZ4_Decompress()
    {
        return NativeCompressions.LZ4.LZ4.Decompress(compressed3, dest);
    }

    [Benchmark]
    [BenchmarkCategory("Decompress")]
    public async Task<int> NativeCompressions_LZ4_DecompressMultiThread()
    {
        writer.ResetWrittenCount();
        await NativeCompressions.LZ4.LZ4.DecompressAsync(compressed4, writer);
        return writer.WrittenCount;
    }

    [Benchmark]
    [BenchmarkCategory("Decompress")]
    public int NativeCompressions_Zstandard_Decompress_Default()
    {
        return NativeCompressions.Zstandard.Zstandard.Decompress(compressed5, dest);
    }

    [Benchmark]
    [BenchmarkCategory("Decompress")]
    public int NativeCompressions_Zstandard_Decompress_Minus4()
    {
        return NativeCompressions.Zstandard.Zstandard.Decompress(compressed7, dest);
    }

    [Benchmark]
    [BenchmarkCategory("Decompress")]
    public int BrotliDecoder_TryDecompress()
    {
        BrotliDecoder.TryDecompress(compressed8, dest, out var bytesWritten);
        return bytesWritten;
    }


    [Benchmark]
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
