namespace NativeCompressions.Internal;

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

    public static void InvalidContextNullException()
    {
        throw new InvalidOperationException("The native context is null. There may be an error in the initialization (such as using default instead of a constructor).");
    }
}
