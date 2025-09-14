namespace NativeCompressions.ZStandard;

public class ZStandardException(string message)
    : Exception(message)
{
    public static ZStandardException FromErrorName(string errorName)
    {
        return new ZStandardException($"ZStandard native operation has been failed, error: {errorName}");
    }
}
