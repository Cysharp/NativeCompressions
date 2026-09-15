using System.Buffers;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;

namespace NativeCompressions.Tests;

// Every LZ4 CompressAsync / DecompressAsync overload, every source type, parallel and sequential paths, multi frame.
public class LZ4AsyncApiTest : IDisposable
{
    readonly string tempDir = Path.Combine(Path.GetTempPath(), "NativeCompressions.Tests", Guid.NewGuid().ToString("N"));

    public LZ4AsyncApiTest() => Directory.CreateDirectory(tempDir);

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
        var words = new[] { "lz4 ", "native ", "compression ", "dotnet ", "async ", "\n" };
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
        yield return new object[] { "compressible5m" };
    }

    static byte[] GetInput(string name) => name switch
    {
        "empty" => [],
        "small" => Encoding.UTF8.GetBytes("small async input"),
        "random1m" => Random(1024 * 1024, 81),
        "compressible5m" => Compressible(5 * 1024 * 1024 + 777, 82),
        _ => throw new ArgumentException(name)
    };

    public static IEnumerable<object[]> InputsAndParallelism()
    {
        foreach (var input in Inputs())
        {
            foreach (var dop in new[] { 1, 2, 4 })
            {
                yield return new object[] { input[0], dop };
            }
        }
    }

    static byte[] StreamDecompress(byte[] compressed)
    {
        using var zs = new LZ4Stream(new MemoryStream(compressed), CompressionMode.Decompress);
        var ms = new MemoryStream();
        zs.CopyTo(ms);
        return ms.ToArray();
    }

    static async Task<byte[]> ReadAllAsync(PipeReader reader)
    {
        var ms = new MemoryStream();
        while (true)
        {
            var result = await reader.ReadAsync();
            foreach (var segment in result.Buffer) ms.Write(segment.Span);
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

    static ReadOnlySequence<byte> ToSequence(byte[] data, int segmentSize)
    {
        if (data.Length == 0) return ReadOnlySequence<byte>.Empty;

        Segment? first = null;
        Segment? last = null;
        for (int offset = 0; offset < data.Length; offset += segmentSize)
        {
            var length = Math.Min(segmentSize, data.Length - offset);
            var segment = new Segment(data.AsMemory(offset, length), last == null ? 0 : last.RunningIndex + last.Memory.Length);
            first ??= segment;
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

    // Independent blocks with both checksums, the frame shape the parallel decoder handles.
    static readonly LZ4CompressionOptions ParallelFriendly = LZ4CompressionOptions.Default with
    {
        BlockMode = BlockMode.BlockIndependent,
        BlockSizeID = BlockSizeId.Max64KB,
        ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled,
        BlockChecksumFlag = BlockChecksum.BlockChecksumEnabled,
    };

    // Parallel compression cannot produce a content checksum, so the compress side drops that flag.
    static readonly LZ4CompressionOptions ParallelCompressFriendly = ParallelFriendly with { ContentChecksumFlag = ContentChecksum.NoContentChecksum };

    // ---- CompressAsync

    [Theory]
    [InlineData("small")]
    [InlineData("compressible5m")]
    public async Task CompressAsync_ParallelWithContentChecksum_Throws(string name)
    {
        var data = GetInput(name);
        var options = LZ4CompressionOptions.Default with { ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled };
        var path = TempFile(name + ".checksum.bin");
        await File.WriteAllBytesAsync(path, data);

        // rejected up front for every parallel capable source, regardless of size
        foreach (var dop in new[] { 2, 4 })
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () => await Collect(w => LZ4.CompressAsync((ReadOnlyMemory<byte>)data, w, options, dop)));
            await Assert.ThrowsAsync<NotSupportedException>(async () => await Collect(w => LZ4.CompressAsync(ToSequence(data, 5000), w, options, dop)));
            await Assert.ThrowsAsync<NotSupportedException>(async () => await Collect(w => LZ4.CompressAsync(path, w, options, dop)));
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
            await Assert.ThrowsAsync<NotSupportedException>(async () => await Collect(w => LZ4.CompressAsync(handle, w, options, dop)));
        }

        // sequential keeps the checksum
        foreach (var dop in new int?[] { null, 1 })
        {
            var compressed = await Collect(w => LZ4.CompressAsync((ReadOnlyMemory<byte>)data, w, options, dop));
            Assert.True(LZ4.TryGetFrameInfo(compressed, out var info));
            Assert.Equal(ContentChecksum.ContentChecksumEnabled, info.ContentChecksumFlag);
            Assert.Equal(data, LZ4.Decompress(compressed));
        }
    }

    [Fact]
    public async Task CompressAsync_NullParallelism_IsSequential()
    {
        var data = GetInput("compressible5m");
        var compressed = await Collect(w => LZ4.CompressAsync((ReadOnlyMemory<byte>)data, w));
        Assert.True(LZ4.TryGetFrameInfo(compressed, out var info));
        Assert.Equal(BlockMode.BlockLinked, info.BlockMode); // parallel compression would have switched to independent blocks
        Assert.Equal(data, LZ4.Decompress(compressed));
    }

    [Theory]
    [MemberData(nameof(InputsAndParallelism))]
    public async Task CompressAsync_ReadOnlyMemory(string name, int dop)
    {
        var data = GetInput(name);
        var compressed = await Collect(w => LZ4.CompressAsync((ReadOnlyMemory<byte>)data, w, maxDegreeOfParallelism: dop));
        Assert.Equal(data, LZ4.Decompress(compressed));
        Assert.Equal(data, StreamDecompress(compressed));

        compressed = await Collect(w => LZ4.CompressAsync((ReadOnlyMemory<byte>)data, w, ParallelCompressFriendly with { CompressionLevel = 3 }, maxDegreeOfParallelism: dop));
        Assert.Equal(data, LZ4.Decompress(compressed));
    }

    [Theory]
    [MemberData(nameof(InputsAndParallelism))]
    public async Task CompressAsync_ReadOnlySequence(string name, int dop)
    {
        var data = GetInput(name);
        var compressed = await Collect(w => LZ4.CompressAsync(ToSequence(data, 70_001), w, maxDegreeOfParallelism: dop));
        Assert.Equal(data, LZ4.Decompress(compressed));

        compressed = await Collect(w => LZ4.CompressAsync(ToSequence(data, 5000), w, ParallelCompressFriendly, maxDegreeOfParallelism: dop));
        Assert.Equal(data, LZ4.Decompress(compressed));
    }

    [Theory]
    [MemberData(nameof(InputsAndParallelism))]
    public async Task CompressAsync_SafeFileHandle(string name, int dop)
    {
        var data = GetInput(name);
        var path = TempFile($"{name}.{dop}.bin");
        await File.WriteAllBytesAsync(path, data);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        var compressed = await Collect(w => LZ4.CompressAsync(handle, w, maxDegreeOfParallelism: dop));
        Assert.Equal(data, LZ4.Decompress(compressed));

        compressed = await Collect(w => LZ4.CompressAsync(handle, w, ParallelCompressFriendly, maxDegreeOfParallelism: dop));
        Assert.Equal(data, LZ4.Decompress(compressed));
    }

    [Fact]
    public async Task CompressAsync_SafeFileHandle_WithOffset()
    {
        var header = Encoding.ASCII.GetBytes("HEADER-TO-SKIP-");
        var data = GetInput("compressible5m");
        var path = TempFile("offset.bin");
        await File.WriteAllBytesAsync(path, header.Concat(data).ToArray());

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        foreach (var dop in new[] { 1, 4 })
        {
            var compressed = await Collect(w => LZ4.CompressAsync(handle, header.Length, w, maxDegreeOfParallelism: dop));
            Assert.Equal(data, LZ4.Decompress(compressed));
        }
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task CompressAsync_Stream_AllKinds(string name)
    {
        var data = GetInput(name);

        var compressed = await Collect(w => LZ4.CompressAsync(new MemoryStream(data), w));
        Assert.Equal(data, LZ4.Decompress(compressed));

        var path = TempFile(name + ".stream.bin");
        var prefix = Encoding.ASCII.GetBytes("skip");
        await File.WriteAllBytesAsync(path, prefix.Concat(data).ToArray());
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
        {
            fs.Position = prefix.Length;
            compressed = await Collect(w => LZ4.CompressAsync(fs, w));
            Assert.Equal(data, LZ4.Decompress(compressed));
        }

        compressed = await Collect(w => LZ4.CompressAsync(new NonSeekableStream(data, maxRead: 3001), w, LZ4CompressionOptions.Default with { CompressionLevel = 2 }));
        Assert.Equal(data, LZ4.Decompress(compressed));
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
                await source.Writer.WriteAsync(data.AsMemory(offset, Math.Min(10007, data.Length - offset)));
            }
            await source.Writer.CompleteAsync();
        });

        var compressed = await Collect(w => LZ4.CompressAsync(source.Reader, w));
        await feeding;
        Assert.Equal(data, LZ4.Decompress(compressed));
    }

    [Theory]
    [MemberData(nameof(InputsAndParallelism))]
    public async Task CompressAsync_FilePath_AndFileToFile(string name, int dop)
    {
        var data = GetInput(name);
        var source = TempFile($"{name}.{dop}.src");
        var destination = TempFile($"{name}.{dop}.lz4");
        await File.WriteAllBytesAsync(source, data);

        var compressed = await Collect(w => LZ4.CompressAsync(source, w, maxDegreeOfParallelism: dop));
        Assert.Equal(data, LZ4.Decompress(compressed));

        await LZ4.CompressAsync(source, destination, maxDegreeOfParallelism: dop);
        Assert.Equal(data, LZ4.Decompress(await File.ReadAllBytesAsync(destination)));
    }

    [Fact]
    public async Task CompressAsync_Cancellation()
    {
        var data = GetInput("compressible5m");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var pipe = new Pipe();
            await LZ4.CompressAsync((ReadOnlyMemory<byte>)data, pipe.Writer, cancellationToken: cts.Token);
        });
    }

    // ---- DecompressAsync, sequential and block parallel

    static byte[] MultiFrameInput(out byte[] expected)
    {
        var a = GetInput("compressible5m").AsSpan(0, 700_000).ToArray();
        var b = GetInput("random1m").AsSpan(0, 300_000).ToArray();
        var c = Encoding.UTF8.GetBytes("tail frame");
        var skippable = new byte[8 + 4];
        BitConverter.TryWriteBytes(skippable.AsSpan(0, 4), 0x184D2A50u);
        BitConverter.TryWriteBytes(skippable.AsSpan(4, 4), 4u);
        expected = a.Concat(b).Concat(c).ToArray();
        return LZ4.Compress(a, ParallelFriendly)
            .Concat(skippable)
            .Concat(LZ4.Compress(b, LZ4CompressionOptions.Default with { ContentSize = 1 }))
            .Concat(LZ4.Compress(ReadOnlySpan<byte>.Empty))
            .Concat(LZ4.Compress(c, ParallelFriendly with { BlockSizeID = BlockSizeId.Max256KB }))
            .ToArray();
    }

    [Theory]
    [MemberData(nameof(InputsAndParallelism))]
    public async Task DecompressAsync_ReadOnlyMemory(string name, int dop)
    {
        var data = GetInput(name);
        foreach (var compressed in new[] { LZ4.Compress(data), LZ4.Compress(data, ParallelFriendly) })
        {
            Assert.Equal(data, await Collect(w => LZ4.DecompressAsync((ReadOnlyMemory<byte>)compressed, w, maxDegreeOfParallelism: dop)));
            Assert.Equal(data, await Collect(w => LZ4.DecompressAsync((ReadOnlyMemory<byte>)compressed, w, LZ4DecompressionOptions.Default, dop)));
        }
    }

    [Theory]
    [MemberData(nameof(InputsAndParallelism))]
    public async Task DecompressAsync_ReadOnlySequence(string name, int dop)
    {
        var data = GetInput(name);
        foreach (var compressed in new[] { LZ4.Compress(data), LZ4.Compress(data, ParallelFriendly) })
        {
            Assert.Equal(data, await Collect(w => LZ4.DecompressAsync(ToSequence(compressed, 333), w, maxDegreeOfParallelism: dop)));
            Assert.Equal(data, await Collect(w => LZ4.DecompressAsync(ToSequence(compressed, 100_000), w, maxDegreeOfParallelism: dop)));
        }
    }

    [Theory]
    [MemberData(nameof(InputsAndParallelism))]
    public async Task DecompressAsync_SafeFileHandle(string name, int dop)
    {
        var data = GetInput(name);
        var compressed = LZ4.Compress(data, ParallelFriendly);
        var path = TempFile($"{name}.{dop}.lz4");
        await File.WriteAllBytesAsync(path, compressed);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        Assert.Equal(data, await Collect(w => LZ4.DecompressAsync(handle, w, maxDegreeOfParallelism: dop)));

        // the handle is still open and usable afterwards
        Assert.Equal(data, await Collect(w => LZ4.DecompressAsync(handle, w, maxDegreeOfParallelism: dop)));
    }

    [Fact]
    public async Task DecompressAsync_SafeFileHandle_WithOffset()
    {
        var header = Encoding.ASCII.GetBytes("HEADER-TO-SKIP-");
        var data = GetInput("compressible5m");
        var compressed = LZ4.Compress(data, ParallelFriendly);
        var path = TempFile("offset.lz4");
        await File.WriteAllBytesAsync(path, header.Concat(compressed).ToArray());

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        foreach (var dop in new[] { 1, 4 })
        {
            Assert.Equal(data, await Collect(w => LZ4.DecompressAsync(handle, header.Length, w, maxDegreeOfParallelism: dop)));
        }
    }

    [Theory]
    [MemberData(nameof(InputsAndParallelism))]
    public async Task DecompressAsync_Stream_AllKinds(string name, int dop)
    {
        var data = GetInput(name);
        var compressed = LZ4.Compress(data, ParallelFriendly);

        Assert.Equal(data, await Collect(w => LZ4.DecompressAsync(new MemoryStream(compressed), w, maxDegreeOfParallelism: dop)));

        var path = TempFile($"{name}.{dop}.stream.lz4");
        await File.WriteAllBytesAsync(path, compressed);
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
        {
            Assert.Equal(data, await Collect(w => LZ4.DecompressAsync(fs, w, maxDegreeOfParallelism: dop)));
        }

        Assert.Equal(data, await Collect(w => LZ4.DecompressAsync(new NonSeekableStream(compressed, maxRead: 97), w, maxDegreeOfParallelism: dop)));
    }

    [Theory]
    [MemberData(nameof(InputsAndParallelism))]
    public async Task DecompressAsync_PipeReader(string name, int dop)
    {
        var data = GetInput(name);
        var compressed = LZ4.Compress(data, ParallelFriendly);

        var source = new Pipe();
        var feeding = Task.Run(async () =>
        {
            for (int offset = 0; offset < compressed.Length; offset += 1009)
            {
                await source.Writer.WriteAsync(compressed.AsMemory(offset, Math.Min(1009, compressed.Length - offset)));
            }
            await source.Writer.CompleteAsync();
        });

        Assert.Equal(data, await Collect(w => LZ4.DecompressAsync(source.Reader, w, maxDegreeOfParallelism: dop)));
        await feeding;
    }

    [Theory]
    [MemberData(nameof(InputsAndParallelism))]
    public async Task DecompressAsync_FilePath_AndFileToFile(string name, int dop)
    {
        var data = GetInput(name);
        var source = TempFile($"{name}.{dop}.in.lz4");
        var destination = TempFile($"{name}.{dop}.out.bin");
        await File.WriteAllBytesAsync(source, LZ4.Compress(data, ParallelFriendly));

        Assert.Equal(data, await Collect(w => LZ4.DecompressAsync(source, w, maxDegreeOfParallelism: dop)));

        await LZ4.DecompressAsync(source, destination, maxDegreeOfParallelism: dop);
        Assert.Equal(data, await File.ReadAllBytesAsync(destination));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task DecompressAsync_MultipleFrames_AllSources(int dop)
    {
        var compressed = MultiFrameInput(out var expected);
        var path = TempFile($"multi.{dop}.lz4");
        await File.WriteAllBytesAsync(path, compressed);

        Assert.Equal(expected, await Collect(w => LZ4.DecompressAsync((ReadOnlyMemory<byte>)compressed, w, maxDegreeOfParallelism: dop)));
        Assert.Equal(expected, await Collect(w => LZ4.DecompressAsync(ToSequence(compressed, 333), w, maxDegreeOfParallelism: dop)));
        Assert.Equal(expected, await Collect(w => LZ4.DecompressAsync(new MemoryStream(compressed), w, maxDegreeOfParallelism: dop)));
        Assert.Equal(expected, await Collect(w => LZ4.DecompressAsync(new NonSeekableStream(compressed, 97), w, maxDegreeOfParallelism: dop)));
        Assert.Equal(expected, await Collect(w => LZ4.DecompressAsync(path, w, maxDegreeOfParallelism: dop)));

        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous))
        {
            Assert.Equal(expected, await Collect(w => LZ4.DecompressAsync(handle, w, maxDegreeOfParallelism: dop)));
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
        Assert.Equal(expected, await Collect(w => LZ4.DecompressAsync(source.Reader, w, maxDegreeOfParallelism: dop)));
        await feeding;

        // one-shot and stream agree
        Assert.Equal(expected, LZ4.Decompress(compressed));
        Assert.Equal(expected, StreamDecompress(compressed));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task DecompressAsync_EmptyInput_WritesNothing(int dop)
    {
        Assert.Empty(await Collect(w => LZ4.DecompressAsync(ReadOnlyMemory<byte>.Empty, w, maxDegreeOfParallelism: dop)));
        Assert.Empty(await Collect(w => LZ4.DecompressAsync(ReadOnlySequence<byte>.Empty, w, maxDegreeOfParallelism: dop)));
        Assert.Empty(await Collect(w => LZ4.DecompressAsync(new MemoryStream(), w, maxDegreeOfParallelism: dop)));
        Assert.Empty(await Collect(w => LZ4.DecompressAsync(new NonSeekableStream([], 10), w, maxDegreeOfParallelism: dop)));

        var path = TempFile($"empty.{dop}.lz4");
        await File.WriteAllBytesAsync(path, []);
        Assert.Empty(await Collect(w => LZ4.DecompressAsync(path, w, maxDegreeOfParallelism: dop)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task DecompressAsync_BadInput_Throws(int dop)
    {
        var good = LZ4.Compress(GetInput("compressible5m"), ParallelFriendly);
        var truncated = good.AsSpan(0, good.Length / 2).ToArray();
        var trailing = good.Concat(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).ToArray();
        var garbage = Random(4096, 99);
        var corruptBlock = good.ToArray();
        corruptBlock[good.Length / 2] ^= 0xFF;

        foreach (var bad in new[] { truncated, trailing, garbage, corruptBlock })
        {
            await Assert.ThrowsAsync<LZ4Exception>(async () => await Collect(w => LZ4.DecompressAsync((ReadOnlyMemory<byte>)bad, w, maxDegreeOfParallelism: dop)));
            await Assert.ThrowsAsync<LZ4Exception>(async () => await Collect(w => LZ4.DecompressAsync(ToSequence(bad, 1000), w, maxDegreeOfParallelism: dop)));
            await Assert.ThrowsAsync<LZ4Exception>(async () => await Collect(w => LZ4.DecompressAsync(new NonSeekableStream(bad, 1000), w, maxDegreeOfParallelism: dop)));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task DecompressAsync_VerifiesChecksums_InEveryPath(int dop)
    {
        var data = GetInput("compressible5m");
        var compressed = LZ4.Compress(data, ParallelFriendly);

        // flip a byte in the content checksum (last 4 bytes of the frame)
        var badContent = compressed.ToArray();
        badContent[^1] ^= 0xFF;

        // flip a byte inside the first block payload, its block checksum no longer matches
        var badBlock = compressed.ToArray();
        badBlock[40] ^= 0xFF;

        foreach (var bad in new[] { badContent, badBlock })
        {
            await Assert.ThrowsAsync<LZ4Exception>(async () => await Collect(w => LZ4.DecompressAsync((ReadOnlyMemory<byte>)bad, w, maxDegreeOfParallelism: dop)));
            Assert.Throws<LZ4Exception>(() => LZ4.Decompress(bad));
        }

        // SkipChecksums accepts the corrupt content checksum in every path
        var skip = LZ4DecompressionOptions.Default with { SkipChecksums = true };
        Assert.Equal(data, await Collect(w => LZ4.DecompressAsync((ReadOnlyMemory<byte>)badContent, w, skip, dop)));
        Assert.Equal(data, LZ4.Decompress(badContent, skip));
    }

    [Fact]
    public async Task DecompressAsync_WithDictionary()
    {
        var data = GetInput("compressible5m");
        using var dict = LZ4Dictionary.Create(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("lz4 native compression dotnet async ", 64))), 9);
        var options = LZ4DecompressionOptions.Default with { Dictionary = dict };

        foreach (var compressed in new[]
        {
            LZ4.Compress(data, LZ4CompressionOptions.Default with { Dictionary = dict }),
            LZ4.Compress(data, ParallelFriendly with { Dictionary = dict }),
        })
        {
            foreach (var dop in new[] { 1, 4 })
            {
                Assert.Equal(data, await Collect(w => LZ4.DecompressAsync((ReadOnlyMemory<byte>)compressed, w, options, dop)));
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task CompressAsync_WithDictionary_ParallelAndSequential(int dop)
    {
        var data = GetInput("compressible5m");
        using var dict = LZ4Dictionary.Create(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("lz4 native compression dotnet async ", 64))), 11);
        var options = LZ4CompressionOptions.Default with { Dictionary = dict };

        var compressed = await Collect(w => LZ4.CompressAsync((ReadOnlyMemory<byte>)data, w, options, maxDegreeOfParallelism: dop));
        Assert.True(LZ4.TryGetFrameInfo(compressed, out var info));
        Assert.Equal(11u, info.DictionaryID);

        var decompressionOptions = LZ4DecompressionOptions.Default with { Dictionary = dict };
        Assert.Equal(data, LZ4.Decompress(compressed, decompressionOptions));
        Assert.Equal(data, await Collect(w => LZ4.DecompressAsync((ReadOnlyMemory<byte>)compressed, w, decompressionOptions, maxDegreeOfParallelism: 4)));
        Assert.Equal(data, await Collect(w => LZ4.DecompressAsync((ReadOnlyMemory<byte>)compressed, w, decompressionOptions, maxDegreeOfParallelism: 1)));
    }

    [Fact]
    public async Task RoundTrip_AsyncBothWays_Parallel()
    {
        var data = GetInput("compressible5m");
        var compressed = await Collect(w => LZ4.CompressAsync((ReadOnlyMemory<byte>)data, w, maxDegreeOfParallelism: 4));
        Assert.True(LZ4.TryGetFrameInfo(compressed, out var info));
        Assert.Equal(BlockMode.BlockIndependent, info.BlockMode);

        var decompressed = await Collect(w => LZ4.DecompressAsync(ToSequence(compressed, 4096), w, maxDegreeOfParallelism: 4));
        Assert.Equal(data, decompressed);
    }
}
