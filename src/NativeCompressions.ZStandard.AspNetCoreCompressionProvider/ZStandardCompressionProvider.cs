using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;

namespace NativeCompressions.ZStandard.AspNetCoreCompressionProvider;

public class ZStandardCompressionProvider : ICompressionProvider
{
    private ZStandardCompressionProviderOptions Options { get; }
    public string EncodingName => "zstd";
    public bool SupportsFlush => true;

    public ZStandardCompressionProvider(IOptions<ZStandardCompressionProviderOptions> options)
    {
        Options = options.Value;
    }

    public Stream CreateStream(Stream outputStream)
    {
        throw new NotImplementedException();
    }
}

public class ZStandardCompressionProviderOptions : IOptions<ZStandardCompressionProviderOptions>
{
    //TODO: Any settings?

    public ZStandardCompressionProviderOptions Value => this;
}