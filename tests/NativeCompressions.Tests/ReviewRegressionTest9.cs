using System.Buffers;
using System.IO.Compression;
using System.IO.Pipelines;

namespace NativeCompressions.Tests;

// Regressions from the eighth v1.0 pre-release review. Each test names the review item it covers.
public class ReviewRegressionTest9
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

    // ---- 1. a cancelled token stops the call before any output is handed to the destination

    static byte[] Input(string operation, byte[] data) => operation switch
    {
        "lz4.compress" or "zstd.compress" => data,
        "lz4.decompress" => LZ4.Compress(data),
        "zstd.decompress" => Zstandard.Compress(data),
        _ => throw new ArgumentException(operation),
    };

    static ValueTask Run(string operation, string sourceKind, byte[] input, PipeWriter destination, CancellationToken cancellationToken)
    {
        switch (sourceKind)
        {
            case "memory":
                return operation switch
                {
                    "lz4.compress" => LZ4.CompressAsync((ReadOnlyMemory<byte>)input, destination, cancellationToken: cancellationToken),
                    "lz4.decompress" => LZ4.DecompressAsync((ReadOnlyMemory<byte>)input, destination, cancellationToken: cancellationToken),
                    "zstd.compress" => Zstandard.CompressAsync((ReadOnlyMemory<byte>)input, destination, cancellationToken: cancellationToken),
                    "zstd.decompress" => Zstandard.DecompressAsync((ReadOnlyMemory<byte>)input, destination, cancellationToken: cancellationToken),
                    _ => throw new ArgumentException(operation),
                };
            case "sequence":
                var sequence = new ReadOnlySequence<byte>(input);
                return operation switch
                {
                    "lz4.compress" => LZ4.CompressAsync(sequence, destination, cancellationToken: cancellationToken),
                    "lz4.decompress" => LZ4.DecompressAsync(sequence, destination, cancellationToken: cancellationToken),
                    "zstd.compress" => Zstandard.CompressAsync(sequence, destination, cancellationToken: cancellationToken),
                    "zstd.decompress" => Zstandard.DecompressAsync(sequence, destination, cancellationToken: cancellationToken),
                    _ => throw new ArgumentException(operation),
                };
            case "stream":
                var stream = new MemoryStream(input);
                return operation switch
                {
                    "lz4.compress" => LZ4.CompressAsync(stream, destination, cancellationToken: cancellationToken),
                    "lz4.decompress" => LZ4.DecompressAsync(stream, destination, cancellationToken: cancellationToken),
                    "zstd.compress" => Zstandard.CompressAsync(stream, destination, cancellationToken: cancellationToken),
                    "zstd.decompress" => Zstandard.DecompressAsync(stream, destination, cancellationToken: cancellationToken),
                    _ => throw new ArgumentException(operation),
                };
            default:
                throw new ArgumentException(sourceKind);
        }
    }

    static byte[] Decode(string operation, byte[] output) => operation switch
    {
        "lz4.compress" => LZ4.Decompress(output),
        "zstd.compress" => Zstandard.Decompress(output),
        _ => output,
    };

    public static IEnumerable<object[]> CancelCases()
    {
        foreach (var operation in new[] { "lz4.compress", "lz4.decompress", "zstd.compress", "zstd.decompress" })
        {
            foreach (var source in new[] { "memory", "sequence", "stream" })
            {
                yield return new object[] { operation, source };
            }
        }
    }

    [Theory]
    [MemberData(nameof(CancelCases))]
    public async Task CancelledToken_LeavesNoOutput_RetrySucceeds(string operation, string sourceKind)
    {
        var data = Compressible(1000, 41);
        var input = Input(operation, data);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var output = await Collect(async writer =>
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Run(operation, sourceKind, input, writer, cts.Token));

            // the same destination gets exactly one copy from the retry
            await Run(operation, sourceKind, input, writer, CancellationToken.None);
        });

        Assert.Equal(data, Decode(operation, output));
    }

    // ---- 2. after the output fails, what the codec did not take is readable right away

    // Accepts a number of flushes, then throws on every flush.
    sealed class FailingPipeWriter(int acceptedFlushes) : PipeWriter
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
            throw new IOException("destination is broken");
        }
    }

    static ValueTask RunPipe(string operation, PipeReader source, PipeWriter destination) => operation switch
    {
        "lz4.compress" => LZ4.CompressAsync(source, destination),
        "lz4.decompress" => LZ4.DecompressAsync(source, destination),
        "zstd.compress" => Zstandard.CompressAsync(source, destination),
        "zstd.decompress" => Zstandard.DecompressAsync(source, destination),
        _ => throw new ArgumentException(operation),
    };

    [Theory]
    [InlineData("lz4.compress")]
    [InlineData("lz4.decompress")]
    [InlineData("zstd.compress")]
    [InlineData("zstd.decompress")]
    public async Task PipeReader_LeftoverReadableWhileInputStaysOpen(string operation)
    {
        var data = Compressible(1024 * 1024, 42);
        var input = operation == "lz4.decompress"
            ? LZ4.Compress(data, LZ4CompressionOptions.Default with { AutoFlush = true })
            : Input(operation, data);

        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        await pipe.Writer.WriteAsync(input); // the writer stays open, more could follow

        await Assert.ThrowsAsync<IOException>(async () => await RunPipe(operation, pipe.Reader, new FailingPipeWriter(acceptedFlushes: 0)));

        // the leftover is not marked examined, so the read returns it without waiting for more input
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ReadResult result;
        try
        {
            result = await pipe.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("the read waited for more input although unprocessed input was left");
            throw;
        }

        var remaining = result.Buffer.ToArray();
        pipe.Reader.AdvanceTo(result.Buffer.End);
        Assert.False(result.IsCompleted);
        Assert.NotEmpty(remaining);
        Assert.Equal(input.AsSpan(input.Length - remaining.Length).ToArray(), remaining);

        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    // ---- 3. Flush writes out what an encoder handed in by the caller already holds

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LZ4Stream_Flush_WritesPendingDataOfExternalEncoder(bool async)
    {
        var data = Compressible(10_000, 43);
        using var encoder = new LZ4Encoder();
        var destination = new MemoryStream();

        // the caller fed the encoder before the stream existed, the block is still buffered inside
        var scratch = new byte[encoder.GetMaxCompressedLength(data.Length)];
        var written = encoder.Compress(data, scratch);
        destination.Write(scratch, 0, written);
        var before = destination.Length;

        var stream = new LZ4Stream(destination, encoder, leaveOpen: true);
        if (async) await stream.FlushAsync(TestContext.Current.CancellationToken); else stream.Flush();
        Assert.True(destination.Length > before, "Flush wrote nothing");

        stream.Dispose();
        Assert.Equal(data, LZ4.Decompress(destination.ToArray()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZstandardStream_Flush_WritesPendingDataOfExternalEncoder(bool async)
    {
        var data = Compressible(10_000, 44);
        using var encoder = new ZstandardEncoder();
        var destination = new MemoryStream();

        var scratch = new byte[Zstandard.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, encoder.Compress(data, scratch, out var consumed, out var written, isFinalBlock: false));
        Assert.Equal(data.Length, consumed);
        destination.Write(scratch, 0, written);
        var before = destination.Length;

        var stream = new ZstandardStream(destination, encoder, leaveOpen: true);
        if (async) await stream.FlushAsync(TestContext.Current.CancellationToken); else stream.Flush();
        Assert.True(destination.Length > before, "Flush wrote nothing");

        stream.Dispose();
        Assert.Equal(data, Zstandard.Decompress(destination.ToArray()));
    }

    [Theory]
    [InlineData("lz4")]
    [InlineData("zstd")]
    public void Stream_Flush_BeforeAnyWrite_IsHarmless(string algorithm)
    {
        var data = Compressible(10_000, 45);
        var destination = new MemoryStream();
        Stream stream = algorithm == "lz4"
            ? new LZ4Stream(destination, CompressionMode.Compress, leaveOpen: true)
            : new ZstandardStream(destination, CompressionMode.Compress, leaveOpen: true);

        stream.Flush();
        stream.Write(data, 0, data.Length);
        stream.Dispose();

        Assert.Equal(data, algorithm == "lz4" ? LZ4.Decompress(destination.ToArray()) : Zstandard.Decompress(destination.ToArray()));
    }
}
