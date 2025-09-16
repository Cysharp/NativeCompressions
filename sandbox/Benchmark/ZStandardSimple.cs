using Benchmark.BenchmarkNetUtilities;
using Benchmark.Models;
using NativeCompressions.LZ4;
using NativeCompressions.Zstandard;
using System.Buffers;
using System.Formats.Tar;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace Benchmark;

[PayloadColumn]
public class ZstandardSimpleEncode
{
    byte[] src = default!;
    public byte[] dest = default!;
    public ArrayBufferPipeWriter writer;
    // List<Question> target = default!;

    //[GlobalSetup]
    //public void Init()
    //{
    //    target = JilModelFactory.Create<List<Question>>();
    //    var jsonString = JsonSerializer.Serialize(target);
    //    src = Encoding.UTF8.GetBytes(jsonString);

    //    var maxSize = NativeCompressions.LZ4.LZ4.GetMaxCompressedLength(src.Length, LZ4FrameOptions.Default);
    //    dest = new byte[maxSize];
    //}

    public ZstandardSimpleEncode()
    {
        src = Resources.Silesia;
        var maxSize = NativeCompressions.Zstandard.Zstandard.GetMaxCompressedLength(src.Length);
        dest = new byte[maxSize];
        writer = new ArrayBufferPipeWriter(maxSize);
    }

    [Benchmark]
    public int K4os_LZ4_Encode()
    {
        return K4os.Compression.LZ4.LZ4Codec.Encode(src, dest, K4os.Compression.LZ4.LZ4Level.L00_FAST);
    }

    [Benchmark]
    public int K4os_LZ4_FrameEncode()
    {
        return K4os.Compression.LZ4.Streams.LZ4Frame.Encode(src, dest, K4os.Compression.LZ4.LZ4Level.L00_FAST);
    }

    [Benchmark]
    public int NativeCompressions_LZ4_Compress()
    {
        return NativeCompressions.LZ4.LZ4.Compress(src, dest);
    }

    [Benchmark]
    public async Task<int> NativeCompressions_LZ4_CompressMultiThread()
    {
        writer.ResetWrittenCount();
        await NativeCompressions.LZ4.LZ4.CompressAsync(src, writer, LZ4FrameOptions.Default);
        return (int)writer.WrittenCount;
    }


    [Benchmark]
    public int NativeCompressions_Zstandard_Compress_Default()
    {
        return NativeCompressions.Zstandard.Zstandard.Compress(src, dest);
    }

    [Benchmark]
    public int NativeCompressions_Zstandard_Compress_Minus4()
    {
        return NativeCompressions.Zstandard.Zstandard.Compress(src, dest, -4);
    }

    //[Benchmark]
    //public int NativeCompressions_Zstandard_Compress_Max()
    //{
    //    return NativeCompressions.Zstandard.Zstandard.Compress(src, dest, Zstandard.MaxCompressionLevel);
    //}

    [Benchmark]
    public int NativeCompressions_Zstandard_Compress_Multithread()
    {
        return NativeCompressions.Zstandard.Zstandard.Compress(src, dest, ZstandardCompressionOptions.Default with { NbWorkers = 4 });
    }

    [Benchmark]
    public int BrotliEncoder_TryCompress()
    {
        System.IO.Compression.BrotliEncoder.TryCompress(src, dest, out var bytesWritten);
        return bytesWritten;
    }

    [Benchmark]
    public int GZipStream_Optimal()
    {
        using (var ms = new MemoryStream(src))
        using (var ms2 = new MemoryStream(buffer: dest, writable: true))
        using (var gzip = new GZipStream(ms2, CompressionLevel.Optimal, leaveOpen: true))
        {
            ms.CopyTo(gzip);
            gzip.Close();
            return (int)ms2.Position;
        }
    }

    //public void Tar()
    //{
    //    //new TarEntry
    //    // new TarWriter(
    //}
}

//[PayloadColumn]
//public class Lz4SimpleDecode
//{
//    byte[] srcNativeCompressions = default!;
//    byte[] srcNativeCompressionsMultithread = default!;
//    byte[] srck4os = default!;
//    byte[] srck4osFrame = default!;

//    public byte[] dest = default!;
//    ArrayBufferPipeWriter writer;
//    ArrayBufferWriter<byte> output;

//    public Lz4SimpleDecode()
//    {
//        dest = new byte[Resources.Silesia.Length];
//        writer = new ArrayBufferPipeWriter(dest.Length);
//        output = new ArrayBufferWriter<byte>(dest.Length);

//        var enc = new Lz4SimpleEncode();

//        var written = enc.NativeCompressions_LZ4_Compress();
//        srcNativeCompressions = enc.dest.AsSpan(0, written).ToArray();

//        written = enc.NativeCompressions_LZ4_CompressMultiThread().Result;
//        srcNativeCompressionsMultithread = enc.writer.WrittenSpan.ToArray();

//        written = enc.K4os_LZ4_Encode();
//        srck4os = enc.dest.AsSpan(0, written).ToArray();

//        written = enc.K4os_LZ4_FrameEncode();
//        srck4osFrame = enc.dest.AsSpan(0, written).ToArray();
//    }

//    [Benchmark]
//    public int K4os_LZ4_Decode()
//    {
//        return K4os.Compression.LZ4.LZ4Codec.Decode(srck4os, dest);
//    }

//    [Benchmark]
//    public int K4os_LZ4_FrameDecode()
//    {
//        output.Clear();
//        var d = K4os.Compression.LZ4.Streams.LZ4Frame.Decode(srck4osFrame, output);
//        return d.WrittenCount;
//    }

//    [Benchmark]
//    public int NativeCompressions_LZ4_Decompress()
//    {
//        return NativeCompressions.LZ4.LZ4.Decompress(srcNativeCompressions, dest);
//    }

//    [Benchmark]
//    public async Task<int> NativeCompressions_LZ4_DecompressMultiThread()
//    {
//        writer.ResetWrittenCount();
//        await NativeCompressions.LZ4.LZ4.DecompressAsync(srcNativeCompressionsMultithread, writer);
//        return (int)writer.WrittenCount;
//    }
//}
