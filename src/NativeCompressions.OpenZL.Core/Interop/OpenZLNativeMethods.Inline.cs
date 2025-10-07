using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace NativeCompressions.Interop;

// hand coded methods that defined ZL_INLINE
public static unsafe partial class OpenZLNativeMethods
{
    /// <summary>
    ///  @returns true iff the report contains an error
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ZL_isError(ZL_Result_size_t_u report)
    {
        return report._code != ZL_ErrorCode.ZL_ErrorCode_no_error ? 1 : 0;
    }

    /// <summary>
    ///  @returns true iff the report contains an error (bool version)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ZL_isErrorBool(ZL_Result_size_t_u report)
    {
        return report._code != ZL_ErrorCode.ZL_ErrorCode_no_error;
    }
}
