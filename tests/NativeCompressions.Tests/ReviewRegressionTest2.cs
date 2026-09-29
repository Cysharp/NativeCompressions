using System.Buffers;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;

namespace NativeCompressions.Tests;

// Regressions from the second v1.0 pre-release review. Each test names the review item it covers.
public class ReviewRegressionTest2 : IDisposable
{
    readonly string tempDir = Path.Combine(Path.GetTempPath(), "NativeCompressions.Tests", Guid.NewGuid().ToString("N"));

    public ReviewRegressionTest2() => Directory.CreateDirectory(tempDir);

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
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
        var pipe = new Pipe();
        var reading = ReadAllAsync(pipe.Reader);
        await producer(pipe.Writer);
        await pipe.Writer.CompleteAsync();
        return await reading;
    }

    sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }
        public void SetNext(Segment next) => Next = next;
    }

    static ReadOnlySequence<byte> ToSequence(IEnumerable<ReadOnlyMemory<byte>> memories)
    {
        Segment? first = null;
        Segment? last = null;
        foreach (var memory in memories)
        {
            var segment = new Segment(memory, last == null ? 0 : last.RunningIndex + last.Memory.Length);
            first ??= segment;
            last?.SetNext(segment);
            last = segment;
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    static ReadOnlySequence<byte> ToSequence(byte[] data, int segmentSize)
    {
        var memories = new List<ReadOnlyMemory<byte>>();
        for (var offset = 0; offset < data.Length; offset += segmentSize)
        {
            memories.Add(data.AsMemory(offset, Math.Min(segmentSize, data.Length - offset)));
        }
        return ToSequence(memories);
    }

    // Hands out exactly the requested size, which a PipeWriter is allowed to do.
    sealed class ExactSizePipeWriter : PipeWriter
    {
        readonly MemoryStream written = new();
        byte[] current = [];

        public byte[] ToArray() => written.ToArray();

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            current = new byte[Math.Max(sizeHint, 1)];
            return current;
        }

        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public override void Advance(int bytes)
        {
            written.Write(current, 0, bytes);
            current = [];
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => new(new FlushResult(false, false));
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null) { }
    }

    // Counts and drops what is written.
    sealed class CountingStream : Stream
    {
        public long Count { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Count += count;
    }

    // ---- 1. parallel compression of a sequence whose blocks span several segments

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LZ4_ParallelCompress_SequenceWithSmallSegments(bool autoFlush)
    {
        var data = Random(2 * 1024 * 1024, 21);
        var source = ToSequence(data, 16 * 1024);
        var options = LZ4CompressionOptions.Default with { AutoFlush = autoFlush };

        var compressed = await Collect(w => LZ4.CompressAsync(source, w, options, maxDegreeOfParallelism: 2));
        Assert.Equal(data, LZ4.Decompress(compressed));
    }

    // ---- 2. decoded data must be returned before waiting for more input

    static async Task<int> ReadWithTimeoutAsync(Stream stream, byte[] buffer, bool async, Pipe input)
    {
        var reading = async
            ? stream.ReadAsync(buffer, 0, buffer.Length)
            : Task.Run(() => stream.Read(buffer, 0, buffer.Length));

        var completed = await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(10)));
        if (completed != reading)
        {
            await input.Writer.CompleteAsync(); // release the blocked read
            Assert.Fail("Read waited for more input although decoded data was available.");
        }
        return await reading;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LZ4_Stream_ReturnsCompleteFrameWhileInputStaysOpen(bool async)
    {
        var text = Utf8("hello, stream!");
        var input = new Pipe();
        await input.Writer.WriteAsync(LZ4.Compress(text)); // the writer stays open

        using var reader = new LZ4Stream(input.Reader.AsStream(), CompressionMode.Decompress);
        var buffer = new byte[100];
        var read = await ReadWithTimeoutAsync(reader, buffer, async, input);

        Assert.Equal(text, buffer.AsSpan(0, read).ToArray());
        await input.Writer.CompleteAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zstd_Stream_ReturnsCompleteFrameWhileInputStaysOpen(bool async)
    {
        var text = Utf8("hello, stream!");
        var input = new Pipe();
        await input.Writer.WriteAsync(Zstandard.Compress(text));

        using var reader = new ZstandardStream(input.Reader.AsStream(), CompressionMode.Decompress);
        var buffer = new byte[100];
        var read = await ReadWithTimeoutAsync(reader, buffer, async, input);

        Assert.Equal(text, buffer.AsSpan(0, read).ToArray());
        await input.Writer.CompleteAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LZ4_Stream_ReturnsFlushedDataOfUnfinishedFrame(bool async)
    {
        var text = Utf8("hello, stream!");
        var input = new Pipe();
        using var writer = new LZ4Stream(input.Writer.AsStream(leaveOpen: true), CompressionMode.Compress, leaveOpen: true);
        writer.Write(text, 0, text.Length);
        writer.Flush(); // the frame is still open

        using var reader = new LZ4Stream(input.Reader.AsStream(), CompressionMode.Decompress);
        var buffer = new byte[100];
        var read = await ReadWithTimeoutAsync(reader, buffer, async, input);

        Assert.Equal(text, buffer.AsSpan(0, read).ToArray());
        writer.Dispose(); // ends the frame while the pipe still accepts writes
        await input.Writer.CompleteAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zstd_Stream_ReturnsFlushedDataOfUnfinishedFrame(bool async)
    {
        var text = Utf8("hello, stream!");
        var input = new Pipe();
        using var writer = new ZstandardStream(input.Writer.AsStream(leaveOpen: true), CompressionMode.Compress, leaveOpen: true);
        writer.Write(text, 0, text.Length);
        writer.Flush();

        using var reader = new ZstandardStream(input.Reader.AsStream(), CompressionMode.Decompress);
        var buffer = new byte[100];
        var read = await ReadWithTimeoutAsync(reader, buffer, async, input);

        Assert.Equal(text, buffer.AsSpan(0, read).ToArray());
        writer.Dispose(); // ends the frame while the pipe still accepts writes
        await input.Writer.CompleteAsync();
    }

    // ---- 5. requested destination sizes must satisfy the native API

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(100, 1)]
    [InlineData(2 * 1024 * 1024, 1)]
    [InlineData(2 * 1024 * 1024, 2)]
    public async Task LZ4_CompressAsync_WorksWithExactSizePipeWriter(int size, int dop)
    {
        var data = Random(size, 22);

        var fromMemory = new ExactSizePipeWriter();
        await LZ4.CompressAsync((ReadOnlyMemory<byte>)data, fromMemory, maxDegreeOfParallelism: dop);
        Assert.Equal(data, LZ4.Decompress(fromMemory.ToArray()));

        var fromSequence = new ExactSizePipeWriter();
        await LZ4.CompressAsync(new ReadOnlySequence<byte>(data), fromSequence, maxDegreeOfParallelism: dop);
        Assert.Equal(data, LZ4.Decompress(fromSequence.ToArray()));

        var fromStream = new ExactSizePipeWriter();
        await LZ4.CompressAsync(new MemoryStream(data), fromStream);
        Assert.Equal(data, LZ4.Decompress(fromStream.ToArray()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LZ4_Encoder_CloseWithoutInput_FitsInMaxCompressedLengthOfZero(bool autoFlush)
    {
        using var encoder = new LZ4Encoder(LZ4CompressionOptions.Default with { AutoFlush = autoFlush });
        var buffer = new byte[encoder.GetMaxCompressedLength(0)];

        var written = encoder.Close(buffer);
        Assert.Empty(LZ4.Decompress(buffer.AsSpan(0, written)));
    }

    // ---- 6. a sequence of 2GB or more must not overflow the block offset

    [Fact]
    public async Task LZ4_ParallelCompress_SequenceOver2GB()
    {
        var shared = new byte[1024 * 1024];
        var source = ToSequence(Enumerable.Repeat((ReadOnlyMemory<byte>)shared, 2049));
        Assert.True(source.Length > int.MaxValue);

        var compressed = await Collect(w => LZ4.CompressAsync(source, w, maxDegreeOfParallelism: 2));

        using var reader = new LZ4Stream(new MemoryStream(compressed), CompressionMode.Decompress);
        var counter = new CountingStream();
        reader.CopyTo(counter);
        Assert.Equal(source.Length, counter.Count);
    }

    // ---- 7. a FileStream source must end up positioned after the data that was read

    [Fact]
    public async Task FileStreamSource_PositionAdvances()
    {
        var text = Utf8("hello, stream!");
        var path = Path.Combine(tempDir, "plain.bin");
        File.WriteAllBytes(path, text);

        byte[] lz4;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            lz4 = await Collect(w => LZ4.CompressAsync(fs, w));
            Assert.Equal(fs.Length, fs.Position);
            Assert.Equal(-1, fs.ReadByte());
        }

        byte[] zstd;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            zstd = await Collect(w => Zstandard.CompressAsync(fs, w));
            Assert.Equal(fs.Length, fs.Position);
            Assert.Equal(-1, fs.ReadByte());
        }

        var lz4Path = Path.Combine(tempDir, "data.lz4");
        File.WriteAllBytes(lz4Path, lz4);
        using (var fs = new FileStream(lz4Path, FileMode.Open, FileAccess.Read))
        {
            Assert.Equal(text, await Collect(w => LZ4.DecompressAsync(fs, w)));
            Assert.Equal(fs.Length, fs.Position);
        }

        var zstdPath = Path.Combine(tempDir, "data.zst");
        File.WriteAllBytes(zstdPath, zstd);
        using (var fs = new FileStream(zstdPath, FileMode.Open, FileAccess.Read))
        {
            Assert.Equal(text, await Collect(w => Zstandard.DecompressAsync(fs, w)));
            Assert.Equal(fs.Length, fs.Position);
        }
    }

    [Fact]
    public async Task FileStreamSource_StartsAtPosition()
    {
        var header = Utf8("HEADER");
        var text = Utf8("hello, stream!");
        var path = Path.Combine(tempDir, "with-header.bin");
        File.WriteAllBytes(path, header.Concat(text).ToArray());

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            fs.Position = header.Length;
            Assert.Equal(text, LZ4.Decompress(await Collect(w => LZ4.CompressAsync(fs, w))));
            Assert.Equal(fs.Length, fs.Position);

            fs.Position = header.Length;
            Assert.Equal(text, Zstandard.Decompress(await Collect(w => Zstandard.CompressAsync(fs, w))));
            Assert.Equal(fs.Length, fs.Position);
        }
    }

    // ---- 8. an external decoder that is inside a frame must not accept the end of input

    [Fact]
    public async Task Zstd_DecompressAsync_ExternalDecoderInsideFrame_EmptyRemainderThrows()
    {
        var compressed = Zstandard.Compress(Random(100_000, 23));

        using var decoder = new ZstandardDecoder();
        var status = decoder.Decompress(compressed.AsSpan(0, 10), new byte[1024], out _, out _);
        Assert.Equal(OperationStatus.NeedMoreData, status);

        await Assert.ThrowsAsync<ZstandardException>(async () =>
            await Collect(w => Zstandard.DecompressAsync(ReadOnlyMemory<byte>.Empty, w, decoder)));
    }

    [Fact]
    public async Task Zstd_DecompressAsync_ExternalDecoderFresh_EmptyInputIsEmpty()
    {
        using var decoder = new ZstandardDecoder();
        Assert.Empty(await Collect(w => Zstandard.DecompressAsync(ReadOnlyMemory<byte>.Empty, w, decoder)));
    }
}
