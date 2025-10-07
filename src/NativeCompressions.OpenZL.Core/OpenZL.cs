using static NativeCompressions.Interop.OpenZLNativeMethods;

namespace NativeCompressions;

public static partial class OpenZL
{
    static unsafe OpenZL()
    {
        DefaultEncodingVersion = ZL_getDefaultEncodingVersion();
    }

    public static readonly uint DefaultEncodingVersion;
}
