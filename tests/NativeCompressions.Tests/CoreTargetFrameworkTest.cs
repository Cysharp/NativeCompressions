using System.Runtime.Versioning;

namespace NativeCompressions.Tests;

// Pins down which build of the library the tests are running against, so a CI matrix entry
// cannot silently test the wrong one.
public class CoreTargetFrameworkTest
{
    static string TargetFrameworkOf(Type type)
    {
        var attribute = type.Assembly.GetCustomAttributes(typeof(TargetFrameworkAttribute), false).OfType<TargetFrameworkAttribute>().Single();
        return attribute.FrameworkName;
    }

    [Fact]
    public void LibraryBuildMatchesTestMode()
    {
        var lz4 = TargetFrameworkOf(typeof(LZ4));
        var zstd = TargetFrameworkOf(typeof(Zstandard));
        Assert.Equal(lz4, zstd);

#if TEST_CORE_TFM_OVERRIDE
        // dotnet test -p:TestCoreTfm=netstandard2.1
        Assert.Equal(".NETStandard,Version=v2.1", lz4);
#else
        // the runtime specific build that matches the test process
        var expected = TargetFrameworkOf(typeof(CoreTargetFrameworkTest));
        Assert.Equal(expected, lz4);
#endif
    }
}
