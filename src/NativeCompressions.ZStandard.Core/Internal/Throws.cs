namespace NativeCompressions.ZStandard.Internal;

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