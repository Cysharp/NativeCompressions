using Benchmark.BenchmarkNetUtilities;
using Benchmark.Models;
//using ;
using NativeCompressions.LZ4;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace Benchmark;



[PayloadColumn]
public class Lz4SimpleEncode
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

    public Lz4SimpleEncode()
    {


        src = Resources.Silesia;
        var maxSize = NativeCompressions.LZ4.LZ4.GetMaxCompressedLength(src.Length, LZ4FrameOptions.Default);
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
}

[PayloadColumn]
public class Lz4SimpleDecode
{
    byte[] srcNativeCompressions = default!;
    byte[] srcNativeCompressionsMultithread = default!;
    byte[] srck4os = default!;
    byte[] srck4osFrame = default!;

    public byte[] dest = default!;
    ArrayBufferPipeWriter writer;
    ArrayBufferWriter<byte> output;

    public Lz4SimpleDecode()
    {
        dest = new byte[Resources.Silesia.Length];
        writer = new ArrayBufferPipeWriter(dest.Length);
        output = new ArrayBufferWriter<byte>(dest.Length);

        var enc = new Lz4SimpleEncode();

        var written = enc.NativeCompressions_LZ4_Compress();
        srcNativeCompressions = enc.dest.AsSpan(0, written).ToArray();

        written = enc.NativeCompressions_LZ4_CompressMultiThread().Result;
        srcNativeCompressionsMultithread = enc.writer.WrittenSpan.ToArray();

        written = enc.K4os_LZ4_Encode();
        srck4os = enc.dest.AsSpan(0, written).ToArray();

        written = enc.K4os_LZ4_FrameEncode();
        srck4osFrame = enc.dest.AsSpan(0, written).ToArray();
    }

    [Benchmark]
    public int K4os_LZ4_Decode()
    {
        return K4os.Compression.LZ4.LZ4Codec.Decode(srck4os, dest);
    }

    [Benchmark]
    public int K4os_LZ4_FrameDecode()
    {
        output.Clear();
        var d = K4os.Compression.LZ4.Streams.LZ4Frame.Decode(srck4osFrame, output);
        return d.WrittenCount;
    }

    [Benchmark]
    public int NativeCompressions_LZ4_Decompress()
    {
        return NativeCompressions.LZ4.LZ4.Decompress(srcNativeCompressions, dest);
    }

    [Benchmark]
    public async Task<int> NativeCompressions_LZ4_DecompressMultiThread()
    {
        writer.ResetWrittenCount();
        await NativeCompressions.LZ4.LZ4.DecompressAsync(srcNativeCompressionsMultithread, writer);
        return (int)writer.WrittenCount;
    }
}
