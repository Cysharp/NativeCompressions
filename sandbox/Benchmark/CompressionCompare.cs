using Benchmark.BenchmarkNetUtilities;
using Benchmark.Models;
using NativeCompressions.LZ4;
using NativeCompressions.ZStandard;
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

    int zstdefault = NativeCompressions.ZStandard.ZStandard.DefaultCompressionLevel; // 3
    int zstdmin = -4; // NativeCompressions.ZStandard.ZStdNativeMethods.ZSTD_minCLevel();
    int zstdmax = NativeCompressions.ZStandard.ZStandard.MaxCompressionLevel; // 22

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
        var bytesWritten = NativeCompressions.LZ4.LZ4.Compress(src, dest);
        return bytesWritten;
    }



    [Benchmark]
    public unsafe int ZStandard_Min()
    {
        var bytesWritten = NativeCompressions.ZStandard.ZStandard.Compress(src, dest, zstdmin);
        return bytesWritten;
    }

    [Benchmark]
    public unsafe int ZStandard_Default()
    {
        var bytesWritten = NativeCompressions.ZStandard.ZStandard.Compress(src, dest);
        return bytesWritten;
    }


    [Benchmark]
    public unsafe int ZStandard_Max()
    {
        var bytesWritten = NativeCompressions.ZStandard.ZStandard.Compress(src, dest, zstdmax);
        return bytesWritten;
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


    [Benchmark]
    public unsafe int GZip_Fastest()
    {
        using (var destStream = new MemoryStream(dest))
        using (var gzipStream = new GZipStream(destStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzipStream.Write(src);
            gzipStream.Flush();
            gzipStream.Dispose();
            return (int)destStream.Position;
        }
    }

    [Benchmark]
    public unsafe int GZip_Optimal()
    {
        using (var destStream = new MemoryStream(dest))
        using (var gzipStream = new GZipStream(destStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzipStream.Write(src);
            gzipStream.Flush();
            gzipStream.Dispose();
            return (int)destStream.Position;
        }
    }

    [Benchmark]
    public unsafe int GZip_SmallestSize()
    {
        using (var destStream = new MemoryStream(dest))
        using (var gzipStream = new GZipStream(destStream, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzipStream.Write(src);
            gzipStream.Flush();
            gzipStream.Dispose();
            return (int)destStream.Position;
        }
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
