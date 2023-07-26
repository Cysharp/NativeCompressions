namespace NativeCompressions.Lz4.AspNetCoreCompressionProvider.Tests;

public class Lz4CompressionProviderTest
{
    [Fact]
    public void BasicTest()
    {
        var span = "Lz4CompressionProvider test"u8;
        var provider = new Lz4CompressionProvider(new Lz4CompressionProviderOptions());

        using var memoryStream = new MemoryStream();
        using var compressionStream = provider.CreateStream(memoryStream);
        compressionStream.Write(span);
    }
}