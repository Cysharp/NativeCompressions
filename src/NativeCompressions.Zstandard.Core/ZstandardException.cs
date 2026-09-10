namespace NativeCompressions;

public class ZstandardException(string message)
    : Exception(message)
{
    internal static ZstandardException FromErrorName(string errorName)
    {
        return new ZstandardException($"Zstandard native operation has been failed, error: {errorName}");
    }
}
