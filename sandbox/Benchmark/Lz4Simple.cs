using Benchmark.Models;
using System.Text;
using System.Text.Json;

namespace Benchmark;

public class Lz4Simple
{
    byte[] src = default!;
    public byte[] dest = default!;

    List<Question> target = default!;
    
    [GlobalSetup]
    public void Init()
    {
        target = JilModelFactory.Create<List<Question>>();
        var jsonString = JsonSerializer.Serialize(target);
        src = Encoding.UTF8.GetBytes(jsonString);

        var maxSize = NativeCompressions.Lz4.LZ4Encoder.GetMaxBlockCompressedLength(src.Length);
        dest = new byte[maxSize];
    }

    [Benchmark]
    public int K4os_Lz4_Encode()
    {
        return K4os.Compression.LZ4.LZ4Codec.Encode(src, dest, K4os.Compression.LZ4.LZ4Level.L00_FAST);
    }

    [Benchmark]
    public int Lz4Net_Encode()
    {
        return LZ4.LZ4Codec.Encode(src, 0, src.Length, dest, 0, dest.Length);
    }

    [Benchmark]
    public int NativeCompressions_Lz4_TryCompress()
    {
        NativeCompressions.Lz4.LZ4Encoder.TryCompress(src, dest, out var bytesWritten);
        return bytesWritten;
    }
}