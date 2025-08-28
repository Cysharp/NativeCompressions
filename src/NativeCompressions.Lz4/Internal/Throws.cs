using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NativeCompressions.LZ4.Internal;

internal static class Throws
{
    public static void ObjectDisposedException()
    {
        throw new ObjectDisposedException("");
    }

    public static void ArgumentOutOfRangeException(string? paramName)
    {
        throw new ArgumentOutOfRangeException(paramName);
    }
}
