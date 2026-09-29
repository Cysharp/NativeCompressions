using System.Buffers;
using System.IO.Pipelines;
using Microsoft.Win32.SafeHandles;

namespace NativeCompressions.Tests;

// Regressions from the fifth v1.0 pre-release review. Each test names the review item it covers.
public class ReviewRegressionTest6 : IDisposable
{
    readonly string tempDir = Path.Combine(Path.GetTempPath(), "NativeCompressions.Tests", Guid.NewGuid().ToString("N"));

    public ReviewRegressionTest6() => Directory.CreateDirectory(tempDir);

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

    static byte[] Compressible(int size, int seed)
    {
        var bytes = Random(size, seed);
        for (var i = 4096; i < bytes.Length; i += 8192)
        {
            Array.Copy(bytes, i - 4096, bytes, i, Math.Min(4096, bytes.Length - i));
        }
        return bytes;
    }

    static async Task<byte[]> ReadAllAsync(PipeReader reader)
    {
        var ms = new MemoryStream();
        while (true)
        {
            var result = await reader.ReadAsync();
            foreach (var segment in result.Buffer) ms.Write(segment.ToArray(), 0, segment.Length);
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) break;
        }
        await reader.CompleteAsync();
        return ms.ToArray();
    }

    static async Task<byte[]> Collect(Func<PipeWriter, ValueTask> producer)
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        var reading = ReadAllAsync(pipe.Reader);
        await producer(pipe.Writer);
        await pipe.Writer.CompleteAsync();
        return await reading;
    }

    // Reads until the expected amount has arrived, while the input of the decoder stays open.
    static async Task<byte[]> ReceiveAsync(PipeReader output, int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new MemoryStream();
        try
        {
            while (received.Length < expected)
            {
                var result = await output.ReadAsync(timeout.Token);
                foreach (var segment in result.Buffer) received.Write(segment.ToArray(), 0, segment.Length);
                output.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"only {received.Length} of {expected} bytes arrived while the input stayed open");
        }
        return received.ToArray();
    }

    // ---- 1. everything decoded so far is delivered before the decoder waits for more input

    [Theory]
    [InlineData(100)]
    [InlineData(65_536)]
    [InlineData(65_537)]
    [InlineData(131_072)]
    [InlineData(300_000)]
    public async Task LZ4_DecompressAsync_DeliversEverythingOfOpenFrame(int size)
    {
        var data = Compressible(size, 91);
        using var encoder = new LZ4Encoder(LZ4CompressionOptions.Default with { AutoFlush = true, BlockSizeID = BlockSizeId.Max64KB });
        var buffer = new byte[encoder.GetMaxCompressedLength(size)];

        var input = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        var output = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        var written = encoder.Compress(data, buffer); // the frame stays open
        await input.Writer.WriteAsync(buffer.AsMemory(0, written));

        var decompressing = LZ4.DecompressAsync(input.Reader, output.Writer).AsTask();
        Assert.Equal(data, await ReceiveAsync(output.Reader, size));

        written = encoder.Close(buffer);
        await input.Writer.WriteAsync(buffer.AsMemory(0, written));
        await input.Writer.CompleteAsync();
        await decompressing.WaitAsync(TimeSpan.FromSeconds(10));
        await output.Writer.CompleteAsync();
    }

    [Theory]
    [InlineData(100)]
    [InlineData(65_536)]
    [InlineData(65_537)]
    [InlineData(131_072)]
    [InlineData(300_000)]
    public async Task Zstd_DecompressAsync_DeliversEverythingOfOpenFrame(int size)
    {
        var data = Compressible(size, 92);
        using var encoder = new ZstandardEncoder();
        var buffer = new byte[Zstandard.GetMaxCompressedLength(size) + 64];

        var input = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        var output = new Pipe(new PipeOptions(pauseWriterThreshold: 0));

        Assert.Equal(OperationStatus.Done, encoder.Compress(data, buffer, out _, out var written, isFinalBlock: false));
        await input.Writer.WriteAsync(buffer.AsMemory(0, written));
        Assert.Equal(OperationStatus.Done, encoder.Flush(buffer, out written)); // the frame stays open
        await input.Writer.WriteAsync(buffer.AsMemory(0, written));

        var decompressing = Zstandard.DecompressAsync(input.Reader, output.Writer).AsTask();
        Assert.Equal(data, await ReceiveAsync(output.Reader, size));

        Assert.Equal(OperationStatus.Done, encoder.Close(buffer, out written));
        await input.Writer.WriteAsync(buffer.AsMemory(0, written));
        await input.Writer.CompleteAsync();
        await decompressing.WaitAsync(TimeSpan.FromSeconds(10));
        await output.Writer.CompleteAsync();
    }

    // ---- 3. StableDst is not passed on by the APIs that choose the destination themselves

    [Fact]
    public async Task LZ4_StableDst_IsIgnoredByHighLevelApis()
    {
        var data = Compressible(3 * 1024 * 1024, 93);
        var options = LZ4DecompressionOptions.Default with { StableDst = true };

        // linked blocks refer to the output that came before them
        var compressed = LZ4.Compress(data, LZ4CompressionOptions.Default with { BlockMode = BlockMode.BlockLinked, BlockSizeID = BlockSizeId.Max64KB });

        Assert.Equal(data, LZ4.Decompress(compressed, options));
        Assert.Equal(data, await Collect(w => LZ4.DecompressAsync((ReadOnlyMemory<byte>)compressed, w, options)));

        // every read gets another buffer
        using var stream = new LZ4Stream(new MemoryStream(compressed), options);
        var ms = new MemoryStream();
        var random = new Random(94);
        while (true)
        {
            var buffer = new byte[random.Next(1, 100_000)];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
            random.NextBytes(buffer); // what was handed out is not kept
        }
        Assert.Equal(data, ms.ToArray());
    }

    // ---- 4. an offset at or past the end of the file is an empty source

    [Theory]
    [InlineData(3, 1)]
    [InlineData(20, 1)]
    [InlineData(20, 2)]
    public async Task FileHandle_OffsetPastEnd_IsEmpty(long offset, int dop)
    {
        var path = Path.Combine(tempDir, $"three-{offset}-{dop}.bin");
        File.WriteAllBytes(path, [1, 2, 3]);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
        SafeFileHandle handle = fs.SafeFileHandle;

        Assert.Empty(LZ4.Decompress(await Collect(w => LZ4.CompressAsync(handle, offset, w, maxDegreeOfParallelism: dop))));
        Assert.Empty(Zstandard.Decompress(await Collect(w => Zstandard.CompressAsync(handle, offset, w))));
        Assert.Empty(await Collect(w => LZ4.DecompressAsync(handle, offset, w, maxDegreeOfParallelism: dop)));
        Assert.Empty(await Collect(w => Zstandard.DecompressAsync(handle, offset, w)));
    }

    [Fact]
    public async Task FileHandle_OffsetInsideFile_StartsThere()
    {
        var data = Compressible(100_000, 95);
        var path = Path.Combine(tempDir, "offset.bin");
        File.WriteAllBytes(path, data);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
        var expected = data.AsSpan(1000).ToArray();

        Assert.Equal(expected, LZ4.Decompress(await Collect(w => LZ4.CompressAsync(fs.SafeFileHandle, 1000, w))));
        Assert.Equal(expected, Zstandard.Decompress(await Collect(w => Zstandard.CompressAsync(fs.SafeFileHandle, 1000, w))));
    }
}
