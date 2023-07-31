using System.IO.Compression;
using System.Text;

namespace NativeCompressions.Lz4.AspNetCoreCompressionProvider.Tests;

public class Lz4CompressionProviderTest
{
    [Fact]
    public void BasicTest()
    {
        // Encode
        var provider = new Lz4CompressionProvider(new Lz4CompressionProviderOptions());
        using var writeStream = new MemoryStream();
        using var compressionStream = provider.CreateStream(writeStream);
        compressionStream.Write("Lz4CompressionProvider test"u8);
        compressionStream.Flush();
        writeStream.Position = 0;

        // Decode
        var lz4Stream = new LZ4Stream(writeStream, CompressionMode.Decompress);
        var dest = new byte[1024];
        var length = lz4Stream.Read(dest);
        var str = Encoding.UTF8.GetString(dest[..length]);
        Assert.Equal("Lz4CompressionProvider test", str);
    }
}