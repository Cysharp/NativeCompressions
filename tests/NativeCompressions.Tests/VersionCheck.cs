namespace NativeCompressions.Tests;

// Library loading and version check test.

public class VersionCheck
{
    [Fact]
    public void LZ4Version()
    {
        string version;
        unsafe
        {
            version = new string((sbyte*)LZ4NativeMethods.LZ4_versionString());
        }

        Assert.Equal("1.10.0", version);
    }

    //[Fact]
    //public void ZstdVersion()
    //{
    //    var version = new string((sbyte*)ZStdNativeMethods.ZSTD_versionString());
    //    version.Should().Be("1.5.2");
    //}
}
