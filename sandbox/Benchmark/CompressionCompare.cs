using Benchmark.BenchmarkNetUtilities;
using Benchmark.Models;
using NativeCompressions.Lz4;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Benchmark;

[PayloadColumn]
public class CompressionCompare
{
    byte[] src = default!;
    public byte[] dest = default!;

    List<Question> target = default!;

    int zstdefault = NativeCompressions.ZStandard.ZStdNativeMethods.ZSTD_defaultCLevel(); // 3
    int zstdmin = 1; // NativeCompressions.ZStandard.ZStdNativeMethods.ZSTD_minCLevel();
    int zstdmax = NativeCompressions.ZStandard.ZStdNativeMethods.ZSTD_maxCLevel(); // 22

    public CompressionCompare()
    {
        target = JilModelFactory.Create<List<Question>>();
        var jsonString = JsonSerializer.Serialize(target);
        src = Encoding.UTF8.GetBytes(jsonString);

        var maxSize = src.Length * 2; // maybe ok...
        dest = new byte[maxSize];
    }

    [Benchmark]
    public int NoCompress()
    {
        return src.Length;
    }

    [Benchmark]
    public int LZ4()
    {
        LZ4Encoder.TryCompress(src, dest, out var bytesWritten);
        return bytesWritten;
    }

    [Benchmark]
    public int LZ4HC_Min()
    {
        LZ4Encoder.TryCompressHC(src, dest, out var bytesWritten, LZ4HCCompressionLevel.Min);
        return bytesWritten;
    }

    [Benchmark]
    public int LZ4HC_Default()
    {
        LZ4Encoder.TryCompressHC(src, dest, out var bytesWritten, LZ4HCCompressionLevel.Default);
        return bytesWritten;
    }

    [Benchmark]
    public int LZ4HC_OptMin()
    {
        LZ4Encoder.TryCompressHC(src, dest, out var bytesWritten, LZ4HCCompressionLevel.OptMin);
        return bytesWritten;
    }

    [Benchmark]
    public int LZ4HC_Max()
    {
        LZ4Encoder.TryCompressHC(src, dest, out var bytesWritten, LZ4HCCompressionLevel.Max);
        return bytesWritten;
    }



    [Benchmark]
    public unsafe int ZStandard_Min()
    {
        fixed (byte* s = src)
        fixed (byte* d = dest)
        {
            return (int)NativeCompressions.ZStandard.ZStdNativeMethods.ZSTD_compress(d, (nuint)dest.Length, s, (nuint)src.Length, zstdmin);
        }
    }

    [Benchmark]
    public unsafe int ZStandard_Default()
    {
        fixed (byte* s = src)
        fixed (byte* d = dest)
        {
            return (int)NativeCompressions.ZStandard.ZStdNativeMethods.ZSTD_compress(d, (nuint)dest.Length, s, (nuint)src.Length, zstdefault);
        }
    }


    [Benchmark]
    public unsafe int ZStandard_Max()
    {
        fixed (byte* s = src)
        fixed (byte* d = dest)
        {
            return (int)NativeCompressions.ZStandard.ZStdNativeMethods.ZSTD_compress(d, (nuint)dest.Length, s, (nuint)src.Length, zstdmax);
        }
    }

    [Benchmark]
    public unsafe int Brotli_Min()
    {
        BrotliEncoder.TryCompress(src, dest, out var bytesWritten, BrotliUtils.Quality_Min, BrotliUtils.WindowBits_Default);
        return bytesWritten;
    }

    [Benchmark]
    public unsafe int Brotli_Default()
    {
        BrotliEncoder.TryCompress(src, dest, out var bytesWritten, BrotliUtils.Quality_Default, BrotliUtils.WindowBits_Default);
        return bytesWritten;
    }

    [Benchmark]
    public unsafe int Brotli_Max()
    {
        BrotliEncoder.TryCompress(src, dest, out var bytesWritten, BrotliUtils.Quality_Max, BrotliUtils.WindowBits_Default);
        return bytesWritten;
    }

    internal static partial class BrotliUtils
    {
        public const int WindowBits_Min = 10;
        public const int WindowBits_Default = 22;
        public const int WindowBits_Max = 24;
        public const int Quality_Min = 0;
        public const int Quality_Default = 4;
        public const int Quality_Max = 11;
    }
}
