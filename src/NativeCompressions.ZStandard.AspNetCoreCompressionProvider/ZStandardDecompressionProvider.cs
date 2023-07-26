using Microsoft.AspNetCore.RequestDecompression;

namespace NativeCompressions.ZStandard.AspNetCoreCompressionProvider;

public class ZStandardDecompressionProvider : IDecompressionProvider
{
    public Stream GetDecompressionStream(Stream stream)
    {
        throw new NotImplementedException();
    }
}