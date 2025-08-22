namespace NativeCompressions.LZ4;

public class LZ4Exception(string errorName)
    : Exception($"LZ4 native operation has been failed, error: {errorName}")
{
}