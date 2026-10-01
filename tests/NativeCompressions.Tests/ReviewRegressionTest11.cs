using System.IO.Compression;

namespace NativeCompressions.Tests;

// Regressions from the tenth v1.0 pre-release review. Each test names the review item it covers.
public class ReviewRegressionTest11
{
    static Stream Create(string algorithm, Stream inner, CompressionMode mode, bool leaveOpen) => algorithm switch
    {
        "lz4" => new LZ4Stream(inner, mode, leaveOpen),
        "zstd" => new ZstandardStream(inner, mode, leaveOpen),
        _ => throw new ArgumentException(algorithm),
    };

    // ---- 1. a disposed stream reports that it can neither read nor write

    [Theory]
    [InlineData("lz4", CompressionMode.Compress, true)]
    [InlineData("lz4", CompressionMode.Compress, false)]
    [InlineData("lz4", CompressionMode.Decompress, true)]
    [InlineData("lz4", CompressionMode.Decompress, false)]
    [InlineData("zstd", CompressionMode.Compress, true)]
    [InlineData("zstd", CompressionMode.Compress, false)]
    [InlineData("zstd", CompressionMode.Decompress, true)]
    [InlineData("zstd", CompressionMode.Decompress, false)]
    public void Stream_CanReadCanWrite_FalseAfterDispose(string algorithm, CompressionMode mode, bool leaveOpen)
    {
        var inner = new MemoryStream();
        var stream = Create(algorithm, inner, mode, leaveOpen);

        Assert.Equal(mode == CompressionMode.Decompress, stream.CanRead);
        Assert.Equal(mode == CompressionMode.Compress, stream.CanWrite);
        Assert.False(stream.CanSeek);

        stream.Dispose();

        Assert.False(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.False(stream.CanSeek);

        // the inner stream is untouched when it was left open
        Assert.Equal(leaveOpen, inner.CanRead);
        Assert.Equal(leaveOpen, inner.CanWrite);
    }

    [Theory]
    [InlineData("lz4")]
    [InlineData("zstd")]
    public async Task Stream_CanReadCanWrite_FalseAfterDisposeAsync(string algorithm)
    {
        var writer = Create(algorithm, new MemoryStream(), CompressionMode.Compress, leaveOpen: true);
        Assert.True(writer.CanWrite);
        await writer.DisposeAsync();
        Assert.False(writer.CanWrite);

        var reader = Create(algorithm, new MemoryStream(), CompressionMode.Decompress, leaveOpen: true);
        Assert.True(reader.CanRead);
        await reader.DisposeAsync();
        Assert.False(reader.CanRead);
    }
}
