namespace NativeCompressions.ZStandard;

public class ZStandardException(string errorName)
    : Exception($"ZStandard native operation has been failed, error: {errorName}")
{
}