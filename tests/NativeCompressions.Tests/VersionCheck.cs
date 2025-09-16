//using NativeCompressions.Lz4;
//using NativeCompressions.Zstandard;

//namespace NativeCompressions.Tests
//{
//    // Library loading and version check test.

//    public unsafe class VersionCheck
//    {
//        [Fact]
//        public void LZ4Version()
//        {
//            var version = new string((sbyte*)Lz4NativeMethods.LZ4_versionString());
//            version.Should().Be("1.9.4");
//        }

//        [Fact]
//        public void ZstdVersion()
//        {
//            var version = new string((sbyte*)ZStdNativeMethods.ZSTD_versionString());
//            version.Should().Be("1.5.2");
//        }
//    }
//}