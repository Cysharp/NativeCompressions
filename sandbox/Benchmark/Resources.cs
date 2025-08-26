namespace Benchmark;

public static class Resources
{
    public static byte[] Silesia;

    static Resources()
    {
        var bin = System.IO.File.ReadAllBytes("silesia.tar.lz4");
        Silesia = NativeCompressions.LZ4.LZ4.Decompress(bin, trustedData: true);
    }
}
