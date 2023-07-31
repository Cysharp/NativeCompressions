using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;

namespace NativeCompressions.Lz4.AspNetCoreCompressionProvider;

public class Lz4CompressionProvider : ICompressionProvider
{
    private Lz4CompressionProviderOptions Options { get; }
    public string EncodingName => "lz4";
    public bool SupportsFlush => true;

    public Lz4CompressionProvider(IOptions<Lz4CompressionProviderOptions> options)
    {
        Options = options.Value;
    }

    public Stream CreateStream(Stream outputStream)
    {
        return new LZ4Stream(outputStream, CompressionMode.Compress,leaveOpen: true);
    }
}

public class Lz4CompressionProviderOptions : IOptions<Lz4CompressionProviderOptions>
{
    //TODO: Any settings?

    public Lz4CompressionProviderOptions Value => this;
}