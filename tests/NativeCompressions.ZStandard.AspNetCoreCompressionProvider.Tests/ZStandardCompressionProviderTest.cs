namespace NativeCompressions.ZStandard.AspNetCoreCompressionProvider.Tests;

public class ZStandardCompressionProviderTest
{
    [Fact]
    public void BasicTest()
    {
        var span = "ZStandardCompressionProvider test"u8;
        var provider = new ZStandardCompressionProvider(new ZStandardCompressionProviderOptions());

        using var memoryStream = new MemoryStream();
        using var compressionStream = provider.CreateStream(memoryStream);
        compressionStream.Write(span);
    }
}