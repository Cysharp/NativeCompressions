using System.IO.Compression;

namespace NativeCompressions.Tests;

// Regressions from the ninth v1.0 pre-release review. Each test names the review item it covers.
public class ReviewRegressionTest10 : IDisposable
{
    readonly string tempDir = Path.Combine(Path.GetTempPath(), "NativeCompressions.Tests", Guid.NewGuid().ToString("N"));

    public ReviewRegressionTest10() => Directory.CreateDirectory(tempDir);

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // ---- 1. a cancelled file to file call leaves the destination file as it is

    [Theory]
    [InlineData("lz4.compress")]
    [InlineData("lz4.decompress")]
    [InlineData("zstd.compress")]
    [InlineData("zstd.decompress")]
    public async Task FileToFile_CancelledToken_KeepsExistingDestination(string operation)
    {
        var data = Random(10_000, 51);
        var source = Path.Combine(tempDir, "source");
        var destination = Path.Combine(tempDir, "destination");
        File.WriteAllBytes(source, operation switch
        {
            "lz4.decompress" => LZ4.Compress(data),
            "zstd.decompress" => Zstandard.Compress(data),
            _ => data,
        });
        var existing = Random(500, 52);
        File.WriteAllBytes(destination, existing);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (operation)
            {
                case "lz4.compress": await LZ4.CompressAsync(source, destination, cancellationToken: cts.Token); break;
                case "lz4.decompress": await LZ4.DecompressAsync(source, destination, cancellationToken: cts.Token); break;
                case "zstd.compress": await Zstandard.CompressAsync(source, destination, cancellationToken: cts.Token); break;
                case "zstd.decompress": await Zstandard.DecompressAsync(source, destination, cancellationToken: cts.Token); break;
            }
        });

        Assert.Equal(existing, File.ReadAllBytes(destination));

        // without cancellation the same call replaces the file
        switch (operation)
        {
            case "lz4.compress": await LZ4.CompressAsync(source, destination, cancellationToken: TestContext.Current.CancellationToken); break;
            case "lz4.decompress": await LZ4.DecompressAsync(source, destination, cancellationToken: TestContext.Current.CancellationToken); break;
            case "zstd.compress": await Zstandard.CompressAsync(source, destination, cancellationToken: TestContext.Current.CancellationToken); break;
            case "zstd.decompress": await Zstandard.DecompressAsync(source, destination, cancellationToken: TestContext.Current.CancellationToken); break;
        }
        var written = File.ReadAllBytes(destination);
        Assert.Equal(data, operation switch
        {
            "lz4.compress" => LZ4.Decompress(written),
            "zstd.compress" => Zstandard.Decompress(written),
            _ => written,
        });
    }

    // ---- 2. an empty LZ4 stream closes after a Flush, whatever buffer the Flush rented

    public static IEnumerable<object[]> FlushCloseCases()
    {
        foreach (var autoFlush in new[] { true, false })
            foreach (var checksum in new[] { true, false })
                foreach (var async in new[] { true, false })
                    yield return new object[] { autoFlush, checksum, async };
    }

    [Theory]
    [MemberData(nameof(FlushCloseCases))]
    public async Task LZ4Stream_FlushThenClose_WithoutWrite(bool autoFlush, bool checksum, bool async)
    {
        var options = LZ4CompressionOptions.Default with
        {
            AutoFlush = autoFlush,
            ContentChecksumFlag = checksum ? ContentChecksum.ContentChecksumEnabled : ContentChecksum.NoContentChecksum,
        };
        var destination = new MemoryStream();
        var stream = new LZ4Stream(destination, options, leaveOpen: true);

        if (async)
        {
            await stream.FlushAsync(TestContext.Current.CancellationToken);
            await stream.DisposeAsync();
        }
        else
        {
            stream.Flush();
            stream.Dispose();
        }

        Assert.Empty(LZ4.Decompress(destination.ToArray()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LZ4Stream_FlushThenWriteThenClose(bool async)
    {
        var data = Random(100_000, 53);
        var destination = new MemoryStream();
        var stream = new LZ4Stream(destination, LZ4CompressionOptions.Default with { AutoFlush = true }, leaveOpen: true);

        if (async)
        {
            await stream.FlushAsync(TestContext.Current.CancellationToken);
            await stream.WriteAsync(data, TestContext.Current.CancellationToken);
            await stream.FlushAsync(TestContext.Current.CancellationToken);
            await stream.DisposeAsync();
        }
        else
        {
            stream.Flush();
            stream.Write(data, 0, data.Length);
            stream.Flush();
            stream.Dispose();
        }

        Assert.Equal(data, LZ4.Decompress(destination.ToArray()));
    }
}
