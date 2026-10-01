using System.Buffers;
using System.IO.Compression;
using System.IO.Pipelines;

namespace NativeCompressions.Tests;

// Regressions from the seventh v1.0 pre-release review. Each test names the review item it covers.
public class ReviewRegressionTest8
{
    static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // half of each block repeats, so blocks compress and reference what came before
    static byte[] Compressible(int size, int seed)
    {
        var bytes = Random(size, seed);
        for (var i = 4096; i < bytes.Length; i += 8192)
        {
            Array.Copy(bytes, i - 4096, bytes, i, Math.Min(4096, bytes.Length - i));
        }
        return bytes;
    }

    static byte[] ReadAll(Stream stream)
    {
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    // ---- 1. a decoder created with StableDst is refused by the stream

    [Fact]
    public void LZ4Stream_RejectsStableDstDecoder()
    {
        using var stableDst = new LZ4Decoder(LZ4DecompressionOptions.Default with { StableDst = true });
        var ex = Assert.Throws<ArgumentException>(() => new LZ4Stream(new MemoryStream(), stableDst));
        Assert.Equal("decoder", ex.ParamName);

        var data = Compressible(100_000, 11);
        var compressed = LZ4.Compress(data);

        // the option path turns the setting off instead
        using (var stream = new LZ4Stream(new MemoryStream(compressed), LZ4DecompressionOptions.Default with { StableDst = true }))
        {
            Assert.Equal(data, ReadAll(stream));
        }

        // a decoder without the setting is accepted
        using var plain = new LZ4Decoder();
        using (var stream = new LZ4Stream(new MemoryStream(compressed), plain))
        {
            Assert.Equal(data, ReadAll(stream));
        }
    }

    // ---- 2. the input reader stays usable when the output fails

    // Accepts a number of flushes, then either cancels or throws on every flush.
    sealed class FailingPipeWriter(int acceptedFlushes, bool cancel) : PipeWriter
    {
        byte[] current = [];
        int flushes;

        public override Memory<byte> GetMemory(int sizeHint = 0) => current = new byte[Math.Max(sizeHint, 4096)];
        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public override void Advance(int bytes) { }
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null) { }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (++flushes <= acceptedFlushes) return new(new FlushResult(false, false));
            if (cancel) return new(new FlushResult(isCanceled: true, isCompleted: false));
            throw new IOException("destination is broken");
        }
    }

    static byte[] Input(string operation) => operation switch
    {
        "lz4.compress" or "zstd.compress" => Compressible(1024 * 1024, 21),
        "lz4.decompress" => LZ4.Compress(Compressible(1024 * 1024, 22), LZ4CompressionOptions.Default with { AutoFlush = true }),
        "zstd.decompress" => Zstandard.Compress(Compressible(1024 * 1024, 23)),
        _ => throw new ArgumentException(operation),
    };

    static ValueTask Run(string operation, PipeReader source, PipeWriter destination) => operation switch
    {
        "lz4.compress" => LZ4.CompressAsync(source, destination),
        "lz4.decompress" => LZ4.DecompressAsync(source, destination),
        "zstd.compress" => Zstandard.CompressAsync(source, destination),
        "zstd.decompress" => Zstandard.DecompressAsync(source, destination),
        _ => throw new ArgumentException(operation),
    };

    [Theory]
    [InlineData("lz4.compress", true)]
    [InlineData("lz4.compress", false)]
    [InlineData("lz4.decompress", true)]
    [InlineData("lz4.decompress", false)]
    [InlineData("zstd.compress", true)]
    [InlineData("zstd.compress", false)]
    [InlineData("zstd.decompress", true)]
    [InlineData("zstd.decompress", false)]
    public async Task PipeReader_UsableAfterOutputFails(string operation, bool cancel)
    {
        var input = Input(operation);
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        await pipe.Writer.WriteAsync(input);
        await pipe.Writer.CompleteAsync();

        var failing = new FailingPipeWriter(acceptedFlushes: 0, cancel);
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Run(operation, pipe.Reader, failing));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(async () => await Run(operation, pipe.Reader, failing));
        }

        // the reader is not stuck in a read, and what is left is exactly the tail the codec did not take
        var result = await pipe.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var remaining = result.Buffer.ToArray();
        pipe.Reader.AdvanceTo(result.Buffer.End);
        Assert.True(result.IsCompleted);
        Assert.True(remaining.Length < input.Length, "nothing was consumed before the failure");
        Assert.Equal(input.AsSpan(input.Length - remaining.Length).ToArray(), remaining);

        if (operation == "lz4.compress")
        {
            // the header goes out with the first piece the encoder takes, which is at most one 64KB block
            Assert.InRange(input.Length - remaining.Length, 1, 64 * 1024);
        }

        await pipe.Reader.CompleteAsync();
    }

    [Theory]
    [InlineData("lz4.compress")]
    [InlineData("lz4.decompress")]
    [InlineData("zstd.compress")]
    [InlineData("zstd.decompress")]
    public async Task PipeReader_AdvancedOnSuccessAsBefore(string operation)
    {
        var input = Input(operation);
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        await pipe.Writer.WriteAsync(input);
        await pipe.Writer.CompleteAsync();

        await Run(operation, pipe.Reader, new FailingPipeWriter(acceptedFlushes: int.MaxValue, cancel: false));

        var result = await pipe.Reader.ReadAsync();
        Assert.True(result.IsCompleted);
        Assert.Equal(0, result.Buffer.Length);
        pipe.Reader.AdvanceTo(result.Buffer.End);
        await pipe.Reader.CompleteAsync();
    }

    // ---- 3. a cancelled ReadAsync consumes nothing

    static Stream CreateDecompressStream(string algorithm, Stream source) => algorithm switch
    {
        "lz4" => new LZ4Stream(source, CompressionMode.Decompress),
        "zstd" => new ZstandardStream(source, CompressionMode.Decompress),
        _ => throw new ArgumentException(algorithm),
    };

    static byte[] Compress(string algorithm, byte[] data) => algorithm switch
    {
        "lz4" => LZ4.Compress(data),
        "zstd" => Zstandard.Compress(data),
        _ => throw new ArgumentException(algorithm),
    };

    [Theory]
    [InlineData("lz4")]
    [InlineData("zstd")]
    public async Task Stream_ReadAsync_CancelledToken_ConsumesNothing(string algorithm)
    {
        var data = Compressible(200_000, 31);
        using var stream = CreateDecompressStream(algorithm, new MemoryStream(Compress(algorithm, data)));

        // a first read leaves input in the internal buffer
        var head = new byte[10];
        var got = 0;
        while (got < head.Length)
        {
            var n = await stream.ReadAsync(head.AsMemory(got), TestContext.Current.CancellationToken);
            Assert.NotEqual(0, n);
            got += n;
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadAsync(new byte[100], cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadAsync(new byte[100], 0, 100, cts.Token));

        Assert.Equal(data, head.Concat(ReadAll(stream)).ToArray());
    }

    // ---- 4. GetMaxCompressedLength of the encoder validates its input

    [Fact]
    public void LZ4Encoder_GetMaxCompressedLength_Validates()
    {
        using var encoder = new LZ4Encoder();
        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.GetMaxCompressedLength(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.GetMaxCompressedLength(int.MinValue, includingHeader: false, includingFooter: false));
        Assert.Throws<OverflowException>(() => encoder.GetMaxCompressedLength(int.MaxValue));
        Assert.Throws<OverflowException>(() => encoder.GetMaxCompressedLength(int.MaxValue, includingHeader: false, includingFooter: false));

        foreach (var size in new[] { 0, 1, 100, 65536, 10_000_000 })
        {
            var estimate = encoder.GetMaxCompressedLength(size);
            Assert.True(estimate > size);
            Assert.True(estimate >= encoder.GetMaxCompressedLength(size, includingHeader: false, includingFooter: false));
        }
    }
}
