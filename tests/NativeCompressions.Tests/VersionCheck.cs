namespace NativeCompressions.Tests;

// Library loading and version check test.

public class VersionCheck
{
    [Test]
    public async Task LZ4Version()
    {
        string version;
        unsafe
        {
            version = new string((sbyte*)LZ4NativeMethods.LZ4_versionString());
        }

        await That(version).IsEqualTo("1.10.0");
    }

    //[Fact]
    //public void ZstdVersion()
    //{
    //    var version = new string((sbyte*)ZStdNativeMethods.ZSTD_versionString());
    //    version.Should().Be("1.5.2");
    //}
}
