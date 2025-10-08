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
    public static void ArgumentOutOfRangeException(string? paramName)
    {
        throw new ArgumentOutOfRangeException(paramName);
    }

    [DoesNotReturn]
    public static void InvalidContextNullException()
    {
        throw new InvalidOperationException("The native context is null. There may be an error in the initialization (such as using default instead of a constructor).");
    }
}
