namespace CatalystSmoke.Middle;

public static class Compression
{
    public static int VersionNumber => NativeCompressions.LZ4.VersionNumber;
    public static uint ZstandardVersionNumber => NativeCompressions.Zstandard.VersionNumber;
}
