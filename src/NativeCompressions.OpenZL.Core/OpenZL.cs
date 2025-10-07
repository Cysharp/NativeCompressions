using NativeCompressions.Interop;
using static NativeCompressions.Interop.OpenZLNativeMethods;

namespace NativeCompressions;

public static partial class OpenZL
{
    static unsafe OpenZL()
    {
        DefaultEncodingVersion = ZL_getDefaultEncodingVersion();
    }

    public static readonly uint DefaultEncodingVersion;

    internal static bool IsError(ZL_Result_size_t_u result)
    {
        return ZL_isErrorBool(result);
    }

    internal static void ThrowIfError(ZL_Result_size_t_u result)
    {
        if (IsError(result))
        {
            var error = GetErrorName(result._code);
            throw new OpenZLException(error);
        }
    }

    static unsafe string GetErrorName(ZL_ErrorCode code)
    {
        var name = (sbyte*)ZL_ErrorCode_toString(code);
        return new string(name);
    }
}
