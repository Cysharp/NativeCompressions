namespace NativeCompressions.LZ4;

public class LZ4Exception(string message)
    : Exception(message)
{
    public static LZ4Exception FromErrorName(string errorName)
    {
        return new LZ4Exception($"LZ4 native operation has been failed, error: {errorName}");
    }
}
