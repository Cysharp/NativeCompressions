namespace NativeCompressions.Zstandard;

public class ZstandardException(string message)
    : Exception(message)
{
    public static ZstandardException FromErrorName(string errorName)
    {
        return new ZstandardException($"Zstandard native operation has been failed, error: {errorName}");
    }
}
