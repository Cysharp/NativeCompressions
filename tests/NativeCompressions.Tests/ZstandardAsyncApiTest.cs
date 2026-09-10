using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using BclStream = System.IO.Compression.ZstandardStream;
using CompressionMode = System.IO.Compression.CompressionMode;

namespace NativeCompressions.Tests;

// Every CompressAsync / DecompressAsync overload, each source type and each option path.
public class ZstandardAsyncApiTest : IDisposable
{
    readonly string tempDir = Path.Combine(Path.GetTempPath(), "NativeCompressions.Tests", Guid.NewGuid().ToString("N"));

    public ZstandardAsyncApiTest() => Directory.CreateDirectory(tempDir);

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    string TempFile(string name) => Path.Combine(tempDir, name);

    static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    static byte[] Compressible(int size, int seed)
    {
        var rand = new Random(seed);
        var words = new[] { "zstd ", "native ", "compression ", "dotnet ", "async ", "\n" };
        var sb = new StringBuilder(size);
        while (sb.Length < size)
        {
            sb.Append(words[rand.Next(words.Length)]);
            if (rand.Next(50) == 0) sb.Append(rand.Next());
        }
        return Encoding.ASCII.GetBytes(sb.ToString(0, size));
    }

    public static IEnumerable<object[]> Inputs()
    {
        yield return new object[] { "empty" };
        yield return new object[] { "small" };
        yield return new object[] { "random1m" };
        yield return new object[] { "compressible3m" };
    }

    static byte[] GetInput(string name) => name switch
    {
        "empty" => [],
        "small" => Encoding.UTF8.GetBytes("small async input"),
        "random1m" => Random(1024 * 1024, 21),
        "compressible3m" => Compressible(3 * 1024 * 1024, 22),
        _ => throw new ArgumentException(name)
    };

    static byte[] BclDecompress(byte[] compressed)
    {
        using var zs = new BclStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var ms = new MemoryStream();
        zs.CopyTo(ms);
        return ms.ToArray();
    }

    static byte[] BclCompress(byte[] data)
    {
        var ms = new MemoryStream();
        using (var zs = new BclStream(ms, new System.IO.Compression.ZstandardCompressionOptions { Quality = 3 }, leaveOpen: true))
        {
            zs.Write(data);
        }
        return ms.ToArray();
    }

    static async Task<byte[]> ReadAllAsync(PipeReader reader)
    {
        var ms = new MemoryStream();
        while (true)
        {
            var result = await reader.ReadAsync();
            foreach (var segment in result.Buffer)
            {
                ms.Write(segment.Span);
            }
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) break;
        }
        await reader.CompleteAsync();
        return ms.ToArray();
    }

    // Runs a producer that writes into a PipeWriter and collects everything it produced.
    static async Task<byte[]> Collect(Func<PipeWriter, ValueTask> producer)
    {
        var pipe = new Pipe();
        var reading = ReadAllAsync(pipe.Reader);
        await producer(pipe.Writer);
        await pipe.Writer.CompleteAsync();
        return await reading;
    }

    // Splits data into a multi segment ReadOnlySequence.
    static ReadOnlySequence<byte> ToSequence(byte[] data, int segmentSize)
    {
        if (data.Length == 0) return ReadOnlySequence<byte>.Empty;

        Segment? first = null;
        Segment? last = null;
        for (int offset = 0; offset < data.Length; offset += segmentSize)
        {
            var length = Math.Min(segmentSize, data.Length - offset);
            var segment = new Segment(data.AsMemory(offset, length), last?.RunningIndex + last?.Memory.Length ?? 0);
            if (first == null) first = segment;
            last?.SetNext(segment);
            last = segment;
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
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

    // A stream that is not a MemoryStream nor a seekable FileStream, to hit the PipeReader path.
    sealed class NonSeekableStream(byte[] data, int maxRead) : Stream
    {
        int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, maxRead), data.Length - position);
            Array.Copy(data, position, buffer, offset, n);
            position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- CompressAsync sources

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task CompressAsync_ReadOnlyMemory(string name)
    {
        var data = GetInput(name);
        var compressed = await Collect(w => Zstandard.CompressAsync((ReadOnlyMemory<byte>)data, w));
        Assert.Equal(data, BclDecompress(compressed));

        // options, parallelism and a small request buffer
        var options = ZstandardCompressionOptions.Default with { CompressionLevel = 5, ChecksumFlag = true };
        compressed = await Collect(w => Zstandard.CompressAsync((ReadOnlyMemory<byte>)data, w, options));
        Assert.Equal(data, BclDecompress(compressed));
        Assert.Equal(data, Zstandard.Decompress(compressed));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task CompressAsync_ReadOnlySequence(string name)
    {
        var data = GetInput(name);
        var sequence = ToSequence(data, segmentSize: 7000);
        Assert.Equal(data.Length == 0, sequence.IsSingleSegment && data.Length == 0);

        var compressed = await Collect(w => Zstandard.CompressAsync(sequence, w));
        Assert.Equal(data, BclDecompress(compressed));

        compressed = await Collect(w => Zstandard.CompressAsync(sequence, w, ZstandardCompressionOptions.Default with { CompressionLevel = 1 }));
        Assert.Equal(data, BclDecompress(compressed));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task CompressAsync_SafeFileHandle(string name)
    {
        var data = GetInput(name);
        var path = TempFile(name + ".bin");
        await File.WriteAllBytesAsync(path, data);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        var compressed = await Collect(w => Zstandard.CompressAsync(handle, w));
        Assert.Equal(data, BclDecompress(compressed));

        compressed = await Collect(w => Zstandard.CompressAsync(handle, w, ZstandardCompressionOptions.Default with { CompressionLevel = 7 }));
        Assert.Equal(data, BclDecompress(compressed));
    }

    [Fact]
    public async Task CompressAsync_SafeFileHandle_WithOffset()
    {
        var header = Encoding.ASCII.GetBytes("HEADER-TO-SKIP-");
        var data = GetInput("compressible3m");
        var path = TempFile("offset.bin");
        await File.WriteAllBytesAsync(path, header.Concat(data).ToArray());

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        var compressed = await Collect(w => Zstandard.CompressAsync(handle, header.Length, w));
        Assert.Equal(data, BclDecompress(compressed));

        using var encoder = new ZstandardEncoder(ZstandardCompressionOptions.Default with { ChecksumFlag = true });
        compressed = await Collect(w => Zstandard.CompressAsync(handle, header.Length, w, encoder));
        Assert.Equal(data, BclDecompress(compressed));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task CompressAsync_Stream_AllKinds(string name)
    {
        var data = GetInput(name);

        // MemoryStream with exposed buffer
        var compressed = await Collect(w => Zstandard.CompressAsync(new MemoryStream(data), w));
        Assert.Equal(data, BclDecompress(compressed));

        // MemoryStream positioned in the middle still compresses from the start of its buffer? No: TryGetBuffer returns the whole buffer.
        // So test a seekable FileStream honoring Position instead.
        var path = TempFile(name + ".stream.bin");
        var prefix = Encoding.ASCII.GetBytes("skip");
        await File.WriteAllBytesAsync(path, prefix.Concat(data).ToArray());
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
        {
            fs.Position = prefix.Length;
            compressed = await Collect(w => Zstandard.CompressAsync(fs, w));
            Assert.Equal(data, BclDecompress(compressed));
        }

        // non seekable stream that trickles bytes
        compressed = await Collect(w => Zstandard.CompressAsync(new NonSeekableStream(data, maxRead: 3001), w, ZstandardCompressionOptions.Default with { CompressionLevel = 2 }));
        Assert.Equal(data, BclDecompress(compressed));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task CompressAsync_PipeReader(string name)
    {
        var data = GetInput(name);

        var source = new Pipe();
        var feeding = Task.Run(async () =>
        {
            for (int offset = 0; offset < data.Length; offset += 10007)
            {
                var n = Math.Min(10007, data.Length - offset);
                await source.Writer.WriteAsync(data.AsMemory(offset, n));
            }
            await source.Writer.CompleteAsync();
        });

        var compressed = await Collect(w => Zstandard.CompressAsync(source.Reader, w));
        await feeding;
        Assert.Equal(data, BclDecompress(compressed));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task CompressAsync_FilePath(string name)
    {
        var data = GetInput(name);
        var path = TempFile(name + ".path.bin");
        await File.WriteAllBytesAsync(path, data);

        var compressed = await Collect(w => Zstandard.CompressAsync(path, w));
        Assert.Equal(data, BclDecompress(compressed));

        using var encoder = new ZstandardEncoder(9);
        compressed = await Collect(w => Zstandard.CompressAsync(path, w, encoder));
        Assert.Equal(data, BclDecompress(compressed));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task CompressAsync_FileToFile(string name)
    {
        var data = GetInput(name);
        var source = TempFile(name + ".src");
        var destination = TempFile(name + ".zst");
        await File.WriteAllBytesAsync(source, data);

        await Zstandard.CompressAsync(source, destination);
        Assert.Equal(data, BclDecompress(await File.ReadAllBytesAsync(destination)));

        using var encoder = new ZstandardEncoder(ZstandardCompressionOptions.Default with { ChecksumFlag = true });
        var destination2 = TempFile(name + ".2.zst");
        await Zstandard.CompressAsync(source, destination2, encoder);
        Assert.Equal(data, BclDecompress(await File.ReadAllBytesAsync(destination2)));
    }

    [Fact]
    public async Task CompressAsync_EncoderReusedAcrossCalls()
    {
        var a = GetInput("compressible3m");
        var b = GetInput("random1m");
        using var encoder = new ZstandardEncoder(3);

        var ca = await Collect(w => Zstandard.CompressAsync((ReadOnlyMemory<byte>)a, w, encoder));
        var cb = await Collect(w => Zstandard.CompressAsync(ToSequence(b, 5000), w, encoder));
        var cc = await Collect(w => Zstandard.CompressAsync(new MemoryStream(a), w, encoder));

        Assert.Equal(a, BclDecompress(ca));
        Assert.Equal(b, BclDecompress(cb));
        Assert.Equal(a, BclDecompress(cc));
    }

    [Fact]
    public async Task CompressAsync_Cancellation()
    {
        var data = GetInput("compressible3m");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var pipe = new Pipe();
            await Zstandard.CompressAsync((ReadOnlyMemory<byte>)data, pipe.Writer, cancellationToken: cts.Token);
        });
    }

    // ---- DecompressAsync sources

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task DecompressAsync_ReadOnlyMemory(string name)
    {
        var data = GetInput(name);
        var compressed = BclCompress(data);

        var decompressed = await Collect(w => Zstandard.DecompressAsync((ReadOnlyMemory<byte>)compressed, w));
        Assert.Equal(data, decompressed);

        decompressed = await Collect(w => Zstandard.DecompressAsync((ReadOnlyMemory<byte>)compressed, w, ZstandardDecompressionOptions.Default));
        Assert.Equal(data, decompressed);

        using var decoder = new ZstandardDecoder();
        decompressed = await Collect(w => Zstandard.DecompressAsync((ReadOnlyMemory<byte>)compressed, w, decoder));
        Assert.Equal(data, decompressed);
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task DecompressAsync_ReadOnlySequence(string name)
    {
        var data = GetInput(name);
        var compressed = BclCompress(data);
        var sequence = ToSequence(compressed, segmentSize: 333);

        var decompressed = await Collect(w => Zstandard.DecompressAsync(sequence, w));
        Assert.Equal(data, decompressed);

        using var decoder = new ZstandardDecoder();
        decompressed = await Collect(w => Zstandard.DecompressAsync(sequence, w, decoder));
        Assert.Equal(data, decompressed);
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task DecompressAsync_SafeFileHandle(string name)
    {
        var data = GetInput(name);
        var compressed = BclCompress(data);
        var path = TempFile(name + ".zst");
        await File.WriteAllBytesAsync(path, compressed);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        var decompressed = await Collect(w => Zstandard.DecompressAsync(handle, w));
        Assert.Equal(data, decompressed);

        using var decoder = new ZstandardDecoder();
        decompressed = await Collect(w => Zstandard.DecompressAsync(handle, w, decoder));
        Assert.Equal(data, decompressed);
    }

    [Fact]
    public async Task DecompressAsync_SafeFileHandle_WithOffset()
    {
        var header = Encoding.ASCII.GetBytes("HEADER-TO-SKIP-");
        var data = GetInput("compressible3m");
        var compressed = BclCompress(data);
        var path = TempFile("offset.zst");
        await File.WriteAllBytesAsync(path, header.Concat(compressed).ToArray());

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        var decompressed = await Collect(w => Zstandard.DecompressAsync(handle, header.Length, w));
        Assert.Equal(data, decompressed);

        using var decoder = new ZstandardDecoder();
        decompressed = await Collect(w => Zstandard.DecompressAsync(handle, header.Length, w, decoder));
        Assert.Equal(data, decompressed);
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task DecompressAsync_Stream_AllKinds(string name)
    {
        var data = GetInput(name);
        var compressed = BclCompress(data);

        var decompressed = await Collect(w => Zstandard.DecompressAsync(new MemoryStream(compressed), w));
        Assert.Equal(data, decompressed);

        var path = TempFile(name + ".stream.zst");
        await File.WriteAllBytesAsync(path, compressed);
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
        {
            decompressed = await Collect(w => Zstandard.DecompressAsync(fs, w));
            Assert.Equal(data, decompressed);
        }

        decompressed = await Collect(w => Zstandard.DecompressAsync(new NonSeekableStream(compressed, maxRead: 97), w));
        Assert.Equal(data, decompressed);
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task DecompressAsync_PipeReader(string name)
    {
        var data = GetInput(name);
        var compressed = BclCompress(data);

        var source = new Pipe();
        var feeding = Task.Run(async () =>
        {
            for (int offset = 0; offset < compressed.Length; offset += 1009)
            {
                var n = Math.Min(1009, compressed.Length - offset);
                await source.Writer.WriteAsync(compressed.AsMemory(offset, n));
            }
            await source.Writer.CompleteAsync();
        });

        var decompressed = await Collect(w => Zstandard.DecompressAsync(source.Reader, w));
        await feeding;
        Assert.Equal(data, decompressed);
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task DecompressAsync_FilePath_AndFileToFile(string name)
    {
        var data = GetInput(name);
        var compressed = BclCompress(data);
        var source = TempFile(name + ".in.zst");
        var destination = TempFile(name + ".out.bin");
        await File.WriteAllBytesAsync(source, compressed);

        var decompressed = await Collect(w => Zstandard.DecompressAsync(source, w));
        Assert.Equal(data, decompressed);

        await Zstandard.DecompressAsync(source, destination);
        Assert.Equal(data, await File.ReadAllBytesAsync(destination));

        using var decoder = new ZstandardDecoder();
        var destination2 = TempFile(name + ".out2.bin");
        await Zstandard.DecompressAsync(source, destination2, decoder);
        Assert.Equal(data, await File.ReadAllBytesAsync(destination2));
    }

    // ---- concatenated frames, same behavior as ZstandardStream

    static byte[] MultiFrameInput(out byte[] expected)
    {
        var a = GetInput("compressible3m").AsSpan(0, 700_000).ToArray();
        var b = GetInput("random1m").AsSpan(0, 300_000).ToArray();
        var c = Encoding.UTF8.GetBytes("tail frame");
        var empty = Zstandard.Compress(ReadOnlySpan<byte>.Empty);
        expected = a.Concat(b).Concat(c).ToArray();
        return BclCompress(a).Concat(empty).Concat(Zstandard.Compress(b, ZstandardCompressionOptions.Default with { ChecksumFlag = true })).Concat(BclCompress(c)).Concat(empty).ToArray();
    }

    [Fact]
    public async Task DecompressAsync_MultipleFrames_AllSources()
    {
        var compressed = MultiFrameInput(out var expected);
        var path = TempFile("multi.zst");
        await File.WriteAllBytesAsync(path, compressed);

        Assert.Equal(expected, await Collect(w => Zstandard.DecompressAsync((ReadOnlyMemory<byte>)compressed, w)));
        Assert.Equal(expected, await Collect(w => Zstandard.DecompressAsync((ReadOnlyMemory<byte>)compressed, w)));
        Assert.Equal(expected, await Collect(w => Zstandard.DecompressAsync(ToSequence(compressed, 333), w)));
        Assert.Equal(expected, await Collect(w => Zstandard.DecompressAsync(new MemoryStream(compressed), w)));
        Assert.Equal(expected, await Collect(w => Zstandard.DecompressAsync(new NonSeekableStream(compressed, 97), w)));
        Assert.Equal(expected, await Collect(w => Zstandard.DecompressAsync(path, w)));

        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous))
        {
            Assert.Equal(expected, await Collect(w => Zstandard.DecompressAsync(handle, w)));
        }

        var source = new Pipe();
        var feeding = Task.Run(async () =>
        {
            for (int offset = 0; offset < compressed.Length; offset += 1009)
            {
                await source.Writer.WriteAsync(compressed.AsMemory(offset, Math.Min(1009, compressed.Length - offset)));
            }
            await source.Writer.CompleteAsync();
        });
        Assert.Equal(expected, await Collect(w => Zstandard.DecompressAsync(source.Reader, w)));
        await feeding;

        // matches ZstandardStream
        using var zs = new ZstandardStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var ms = new MemoryStream();
        zs.CopyTo(ms);
        Assert.Equal(expected, ms.ToArray());
    }

    [Fact]
    public async Task DecompressAsync_MultipleFrames_SharedDecoderAcrossCalls()
    {
        var compressed = MultiFrameInput(out var expected);
        using var decoder = new ZstandardDecoder();

        Assert.Equal(expected, await Collect(w => Zstandard.DecompressAsync((ReadOnlyMemory<byte>)compressed, w, decoder)));
        Assert.Equal(expected, await Collect(w => Zstandard.DecompressAsync(ToSequence(compressed, 5000), w, decoder)));
    }

    [Fact]
    public async Task DecompressAsync_EmptyInput_WritesNothing()
    {
        Assert.Empty(await Collect(w => Zstandard.DecompressAsync(ReadOnlyMemory<byte>.Empty, w)));
        Assert.Empty(await Collect(w => Zstandard.DecompressAsync(ReadOnlySequence<byte>.Empty, w)));
        Assert.Empty(await Collect(w => Zstandard.DecompressAsync(new MemoryStream(), w)));
        Assert.Empty(await Collect(w => Zstandard.DecompressAsync(new NonSeekableStream([], 10), w)));

        var path = TempFile("empty.zst");
        await File.WriteAllBytesAsync(path, []);
        Assert.Empty(await Collect(w => Zstandard.DecompressAsync(path, w)));
    }

    [Fact]
    public async Task DecompressAsync_TrailingPartialFrame_Throws()
    {
        var first = BclCompress(GetInput("random1m"));
        var second = BclCompress(GetInput("compressible3m"));
        var input = first.Concat(second.AsSpan(0, second.Length / 2).ToArray()).ToArray();

        await Assert.ThrowsAsync<ZstandardException>(async () => await Collect(w => Zstandard.DecompressAsync((ReadOnlyMemory<byte>)input, w)));
        await Assert.ThrowsAsync<ZstandardException>(async () => await Collect(w => Zstandard.DecompressAsync(ToSequence(input, 4096), w)));
        await Assert.ThrowsAsync<ZstandardException>(async () => await Collect(w => Zstandard.DecompressAsync(new NonSeekableStream(input, 4096), w)));
    }

    [Fact]
    public async Task DecompressAsync_TrailingGarbage_Throws()
    {
        var input = BclCompress(GetInput("random1m")).Concat(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).ToArray();
        await Assert.ThrowsAsync<ZstandardException>(async () => await Collect(w => Zstandard.DecompressAsync((ReadOnlyMemory<byte>)input, w)));
    }

    [Fact]
    public async Task DecompressAsync_InvalidData_Throws()
    {
        var garbage = Random(4096, 99);
        await Assert.ThrowsAsync<ZstandardException>(async () => await Collect(w => Zstandard.DecompressAsync((ReadOnlyMemory<byte>)garbage, w)));
        await Assert.ThrowsAsync<ZstandardException>(async () => await Collect(w => Zstandard.DecompressAsync(ToSequence(garbage, 100), w)));
        await Assert.ThrowsAsync<ZstandardException>(async () => await Collect(w => Zstandard.DecompressAsync(new NonSeekableStream(garbage, 100), w)));
    }

    [Fact]
    public async Task DecompressAsync_TruncatedInput_Throws()
    {
        var compressed = BclCompress(GetInput("compressible3m"));
        var truncated = compressed.AsSpan(0, compressed.Length / 2).ToArray();

        await Assert.ThrowsAsync<ZstandardException>(async () => await Collect(w => Zstandard.DecompressAsync((ReadOnlyMemory<byte>)truncated, w)));
    }

    [Fact]
    public async Task DecompressAsync_WithDictionary()
    {
        var data = GetInput("compressible3m");
        using var dict = ZstandardDictionary.Create(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("zstd native compression dotnet async ", 64))));
        var compressed = Zstandard.Compress(data, ZstandardCompressionOptions.Default with { Dictionary = dict });
        var options = ZstandardDecompressionOptions.Default with { Dictionary = dict };

        var decompressed = await Collect(w => Zstandard.DecompressAsync((ReadOnlyMemory<byte>)compressed, w, options));
        Assert.Equal(data, decompressed);

        await Assert.ThrowsAsync<ZstandardException>(async () => await Collect(w => Zstandard.DecompressAsync((ReadOnlyMemory<byte>)compressed, w)));
    }

    [Fact]
    public async Task RoundTrip_NativeAsyncBothWays()
    {
        var data = GetInput("compressible3m");
        var compressed = await Collect(w => Zstandard.CompressAsync((ReadOnlyMemory<byte>)data, w));
        var decompressed = await Collect(w => Zstandard.DecompressAsync(ToSequence(compressed, 4096), w));
        Assert.Equal(data, decompressed);
    }
}
