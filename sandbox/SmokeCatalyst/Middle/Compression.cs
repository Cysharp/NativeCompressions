namespace CatalystSmoke.Middle;

public static unsafe class Compression
{
    public static uint OpenZLEncodingVersion => NativeCompressions.Interop.OpenZLNativeMethods.ZL_getDefaultEncodingVersion();
    public static int VersionNumber => NativeCompressions.LZ4.VersionNumber;
    public static uint ZstandardVersionNumber => NativeCompressions.Zstandard.VersionNumber;
}
