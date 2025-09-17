using ZstdSharp;
using ZstdSharp.Unsafe;
using NativeCompressions.Zstandard;
using Benchmark.BenchmarkNetUtilities;

namespace Benchmark;


[PayloadColumn]
public class ZstandardSimpleEncode
{
    byte[] src = default!;
    public byte[] dest = default!;

    public ZstandardSimpleEncode()
    {
        src = Resources.Silesia;
        var maxSize = NativeCompressions.Zstandard.Zstandard.GetMaxCompressedLength(src.Length);
        dest = new byte[maxSize];
    }

    [Benchmark]
    public int ZstdSharp_Zstandard_Compress_Default()
    {
        using var compressor = new Compressor();
        return compressor.Wrap((ReadOnlySpan<byte>)src, (Span<byte>)dest);
    }

    [Benchmark]
    public int ZstdSharp_Zstandard_Compress_Minus4()
    {
        using var compressor = new Compressor(-4);
        return compressor.Wrap((ReadOnlySpan<byte>)src, (Span<byte>)dest);
    }

    [Benchmark]
    public int ZstdSharp_Zstandard_Compress_Multithread()
    {
        using var compressor = new Compressor();
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers, 4);
        return compressor.Wrap((ReadOnlySpan<byte>)src, (Span<byte>)dest);
    }

    [Benchmark]
    public int NativeCompressions_Zstandard_Compress_Default()
    {
        return NativeCompressions.Zstandard.Zstandard.Compress(src, dest, ZstandardCompressionOptions.Default);
    }

    [Benchmark]
    public int NativeCompressions_Zstandard_Compress_Minus4()
    {
        return NativeCompressions.Zstandard.Zstandard.Compress(src, dest, ZstandardCompressionOptions.Default with { CompressionLevel = -4 });
    }

    [Benchmark]
    public int NativeCompressions_Zstandard_Compress_Multithread()
    {
        return NativeCompressions.Zstandard.Zstandard.Compress(src, dest, ZstandardCompressionOptions.Default with { NbWorkers = 4 });
    }
}

[PayloadColumn]
public class ZstandardSimpleDecode
{
    byte[] srcNativeCompressions = default!;
    byte[] srcZstdSharp = default!;

    public byte[] dest = default!;

    public ZstandardSimpleDecode()
    {
        dest = new byte[Resources.Silesia.Length];

        var enc = new ZstandardSimpleEncode();

        var written = enc.NativeCompressions_Zstandard_Compress_Default();
        srcNativeCompressions = enc.dest.AsSpan(0, written).ToArray();

        written = enc.ZstdSharp_Zstandard_Compress_Default();
        srcZstdSharp = enc.dest.AsSpan(0, written).ToArray();
    }

    [Benchmark]
    public int ZstdSharp_Zstandard_Decode()
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap((ReadOnlySpan<byte>)srcZstdSharp, (Span<byte>)dest);
    }

    [Benchmark]
    public int NativeCompressions_Zstandard_Decompress()
    {
        return NativeCompressions.Zstandard.Zstandard.Decompress(srcNativeCompressions, dest);
    }
}
