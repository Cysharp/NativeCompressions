using NativeCompressions.ZStandard.Raw;
using System.Runtime.CompilerServices;
using static NativeCompressions.ZStandard.Raw.NativeMethods;

namespace NativeCompressions.ZStandard;

public static partial class ZStandard
{
    static string? version;

    /// <summary>
    /// Gets the version string of the ZStandard library.
    /// </summary>
    public static string Version
    {
        get
        {
            if (version == null)
            {
                unsafe
                {
                    // null-terminated
                    version = new string((sbyte*)ZSTD_versionString());
                }
            }
            return version;
        }
    }
}
