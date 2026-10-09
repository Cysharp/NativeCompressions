using System.Buffers;
using System.IO.Compression;
using System.IO.Pipelines;

namespace NativeCompressions.Tests;

// Regressions from the sixth v1.0 pre-release review. Each test names the review item it covers.
public class ReviewRegressionTest7
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

    // compresses to almost nothing, so the compressed output stays far below the size of one destination buffer
    static byte[] RepeatedText(int size)
    {
        var bytes = new byte[size];
        var text = "The quick brown fox jumps over the lazy dog. "u8;
        for (var i = 0; i < size; i += text.Length)
        {
            text.Slice(0, Math.Min(text.Length, size - i)).CopyTo(bytes.AsSpan(i));
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

    // Reads whatever has arrived so far, and fails when nothing arrives while the input stays open.
    static async Task<byte[]> ReceiveSomeAsync(PipeReader output)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var result = await output.ReadAsync(timeout.Token);
            var received = result.Buffer.ToArray();
            output.AdvanceTo(result.Buffer.End);
            Assert.NotEmpty(received);
            return received;
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("no compressed output arrived while the input stayed open");
            throw;
        }
    }

    // ---- R1. a FlushAsync with an already cancelled token leaves the encoder as it was

    static Stream CreateCompressStream(string algorithm, Stream destination) => algorithm switch
    {
        "lz4" => new LZ4Stream(destination, CompressionMode.Compress, leaveOpen: true),
        "zstd" => new ZstandardStream(destination, CompressionMode.Compress, leaveOpen: true),
        _ => throw new ArgumentException(algorithm),
    };

    static byte[] Decompress(string algorithm, byte[] compressed) => algorithm switch
    {
        "lz4" => LZ4.Decompress(compressed),
        "zstd" => Zstandard.Decompress(compressed),
        _ => throw new ArgumentException(algorithm),
    };

    [Theory]
    [InlineData("lz4", 50)]
    [InlineData("lz4", 300_000)]
    [InlineData("zstd", 50)]
    [InlineData("zstd", 300_000)]
    public async Task Stream_FlushAsync_CancelledToken_KeepsWrittenData(string algorithm, int size)
    {
        var data = Compressible(size, 1);
        var destination = new MemoryStream();
        var stream = CreateCompressStream(algorithm, destination);

        await stream.WriteAsync(data);
        var writtenBefore = destination.Length;

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.FlushAsync(cts.Token));

        // nothing was taken from the encoder, and the frame can still be finished normally
        Assert.Equal(writtenBefore, destination.Length);
        stream.Dispose();
        Assert.Equal(data, Decompress(algorithm, destination.ToArray()));
    }

    [Theory]
    [InlineData("lz4")]
    [InlineData("zstd")]
    public async Task Stream_FlushAsync_CancelledToken_ThenFlushAgain(string algorithm)
    {
        var data = Compressible(100_000, 2);
        var destination = new MemoryStream();
        var stream = CreateCompressStream(algorithm, destination);

        await stream.WriteAsync(data);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.FlushAsync(cts.Token));

        // a flush without cancellation still delivers everything written so far
        await stream.FlushAsync(TestContext.Current.CancellationToken);
        var more = Compressible(50_000, 3);
        await stream.WriteAsync(more);
        stream.Dispose();
        Assert.Equal(data.Concat(more).ToArray(), Decompress(algorithm, destination.ToArray()));
    }

    // ---- R2. compressed output is delivered before the encoder waits for more input

    [Theory]
    [InlineData(128 * 1024)]
    [InlineData(256 * 1024)]
    [InlineData(1024 * 1024)]
    public async Task Zstd_CompressAsync_DeliversOutputWhileInputStaysOpen(int size)
    {
        var data = RepeatedText(size);
        var input = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        var output = new Pipe(new PipeOptions(pauseWriterThreshold: 0));

        var compressing = Zstandard.CompressAsync(input.Reader, output.Writer).AsTask();
        await input.Writer.WriteAsync(data);
        var first = await ReceiveSomeAsync(output.Reader);

        await input.Writer.CompleteAsync();
        await compressing.WaitAsync(TimeSpan.FromSeconds(10));
        await output.Writer.CompleteAsync();
        var rest = await ReadAllAsync(output.Reader);

        Assert.Equal(data, Zstandard.Decompress(first.Concat(rest).ToArray()));
    }

    // Hands out its data, then blocks until it is released. Stands for a connection that has more to say later.
    sealed class GatedStream(byte[] data) : Stream
    {
        readonly TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int position;

        public void Release() => gate.TrySetResult(true);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position == data.Length)
            {
                await gate.Task.WaitAsync(cancellationToken);
                return 0;
            }
            var n = Math.Min(buffer.Length, data.Length - position);
            data.AsMemory(position, n).CopyTo(buffer);
            position += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    [Fact]
    public async Task Zstd_CompressAsync_Stream_DeliversOutputWhileInputStaysOpen()
    {
        var data = RepeatedText(512 * 1024);
        var source = new GatedStream(data);
        var output = new Pipe(new PipeOptions(pauseWriterThreshold: 0));

        var compressing = Zstandard.CompressAsync(source, output.Writer).AsTask();
        var first = await ReceiveSomeAsync(output.Reader);

        source.Release();
        await compressing.WaitAsync(TimeSpan.FromSeconds(10));
        await output.Writer.CompleteAsync();
        var rest = await ReadAllAsync(output.Reader);

        Assert.Equal(data, Zstandard.Decompress(first.Concat(rest).ToArray()));
    }

    // ---- R3. the size given for Close is enough for an empty frame

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void LZ4_GetMaxFlushBufferLength_ClosesEmptyFrame(bool autoFlush, bool checksum)
    {
        var options = LZ4CompressionOptions.Default with
        {
            AutoFlush = autoFlush,
            ContentChecksumFlag = checksum ? ContentChecksum.ContentChecksumEnabled : ContentChecksum.NoContentChecksum,
        };
        using var encoder = new LZ4Encoder(options);
        var data = Compressible(100_000, 6);
        var big = new byte[encoder.GetMaxCompressedLength(data.Length)];

        // a fresh encoder
        CloseEmptyFrame(encoder);

        // after a frame was abandoned
        encoder.Compress(data, big);
        encoder.Reset();
        CloseEmptyFrame(encoder);

        // after a frame was closed
        var written = encoder.Compress(data, big);
        written += encoder.Close(big.AsSpan(written));
        Assert.Equal(data, LZ4.Decompress(big.AsSpan(0, written)));
        CloseEmptyFrame(encoder);

        // the same value is still enough inside a frame
        written = encoder.Compress(data, big);
        var closing = new byte[encoder.GetMaxFlushBufferLength()];
        var closed = encoder.Close(closing);
        Assert.Equal(data, LZ4.Decompress(big.AsSpan(0, written).ToArray().Concat(closing.AsSpan(0, closed).ToArray()).ToArray()));

        static void CloseEmptyFrame(LZ4Encoder encoder)
        {
            var buffer = new byte[encoder.GetMaxFlushBufferLength()];
            var written = encoder.Close(buffer);
            Assert.Empty(LZ4.Decompress(buffer.AsSpan(0, written)));
        }
    }

    [Fact]
    public void LZ4_GetMaxFlushBufferLength_FlushNeedsNoHeaderRoom()
    {
        using var encoder = new LZ4Encoder(LZ4CompressionOptions.Default with { AutoFlush = true });
        var flush = encoder.GetMaxFlushBufferLength(includingFooter: false);
        var close = encoder.GetMaxFlushBufferLength();
        Assert.True(flush < close);
        Assert.Equal(encoder.GetMaxCompressedLength(0), close);
        Assert.Equal(close, encoder.GetMaxFlushBufferLength(includingFooter: true));

        // flushing a fresh encoder writes nothing and needs no room
        Assert.Equal(0, encoder.Flush(new byte[flush]));
    }

    // ---- R4. FrameType is read from frames, not an option for writing them

    [Fact]
    public void LZ4_FrameType_IsNotACompressionOption()
    {
        Assert.Null(typeof(LZ4CompressionOptions).GetProperty("FrameType"));
        Assert.NotNull(typeof(LZ4FrameInfo).GetProperty("FrameType"));

        var frame = new byte[8 + 4];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), 0x184D2A50u);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), 4u);
        Assert.True(LZ4.TryGetFrameInfo(frame, out var info));
        Assert.Equal(FrameType.SkippableFrame, info.FrameType);

        Assert.True(LZ4.TryGetFrameInfo(LZ4.Compress(Random(100, 7)), out info));
        Assert.Equal(FrameType.Frame, info.FrameType);
    }
}
