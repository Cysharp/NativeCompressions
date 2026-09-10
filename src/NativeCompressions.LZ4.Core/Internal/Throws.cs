using System.Diagnostics.CodeAnalysis;

namespace NativeCompressions.Internal;

internal static class Throws
{
    [DoesNotReturn]
    public static void ObjectDisposedException()
    {
        throw new ObjectDisposedException("");
    }

    [DoesNotReturn]
    public static void ObjectDisposedException(string objectName)
    {
        throw new ObjectDisposedException(objectName);
    }

    [DoesNotReturn]
    public static void ArgumentOutOfRangeException(string? paramName)
    {
        throw new ArgumentOutOfRangeException(paramName);
    }
}
