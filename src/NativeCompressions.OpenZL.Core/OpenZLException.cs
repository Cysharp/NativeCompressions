namespace NativeCompressions;

public class OpenZLException(string message)
    : Exception(message)
{
    public static OpenZLException FromErrorName(string errorName)
    {
        return new OpenZLException($"OpenZL native operation has been failed, error: {errorName}");
    }
}
