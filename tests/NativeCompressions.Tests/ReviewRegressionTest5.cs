using System.Buffers;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;

namespace NativeCompressions.Tests;

// Regressions from the fourth v1.0 pre-release review. Each test names the review item it covers.
public class ReviewRegressionTest5 : IDisposable
{
    readonly string tempDir = Path.Combine(Path.GetTempPath(), "NativeCompressions.Tests", Guid.NewGuid().ToString("N"));

    public ReviewRegressionTest5() => Directory.CreateDirectory(tempDir);

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
        var pipe = new Pipe();
        var reading = ReadAllAsync(pipe.Reader);
        await producer(pipe.Writer);
        await pipe.Writer.CompleteAsync();
        return await reading;
    }

    // ---- 1. an encoder keeps its dictionary usable, whatever happens to the dictionary object

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Zstd_DictionaryDisposedWhileEncoderUsesIt(int workers)
    {
        var content = Random(32 * 1024, 71);
        var data = content.AsSpan(0, 16 * 1024).ToArray().Concat(Compressible(6 * 1024 * 1024, 72)).ToArray();
        var dictionary = ZstandardDictionary.Create(content);
        var options = ZstandardCompressionOptions.Default with { NbWorkers = workers, Dictionary = dictionary, ChecksumFlag = true };

        using var encoder = new ZstandardEncoder(options);
        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();

        // start the frame, then the owner lets go of the dictionary
        var source = data.AsSpan();
        var status = encoder.Compress(source.Slice(0, 3 * 1024 * 1024), buffer, out var consumed, out var written, isFinalBlock: false);
        Assert.NotEqual(OperationStatus.InvalidData, status);
        ms.Write(buffer, 0, written);
        source = source.Slice(consumed);

        dictionary.Dispose();
        Assert.True(dictionary.IsDisposed);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        while (true)
        {
            status = encoder.Compress(source, buffer, out consumed, out written, isFinalBlock: true);
            Assert.NotEqual(OperationStatus.InvalidData, status);
            ms.Write(buffer, 0, written);
            source = source.Slice(consumed);
            if (status == OperationStatus.Done) break;
        }

        using var same = ZstandardDictionary.Create(content);
        Assert.Equal(data, Zstandard.Decompress(ms.ToArray(), ZstandardDecompressionOptions.Default with { Dictionary = same }));

        // a disposed dictionary is still refused for new use
        Assert.Throws<ObjectDisposedException>(() => new ZstandardEncoder(options));
        Assert.Throws<ObjectDisposedException>(() => encoder.Reset(options));
    }

    [Fact]
    public void Zstd_DictionarySharedByManyEncoders()
    {
        var content = Random(32 * 1024, 73);
        var data = content.AsSpan(0, 16 * 1024).ToArray();
        var dictionary = ZstandardDictionary.Create(content);
        var options = ZstandardCompressionOptions.Default with { Dictionary = dictionary };

        var encoders = Enumerable.Range(0, 8).Select(_ => new ZstandardEncoder(options)).ToArray();
        dictionary.Dispose();
        dictionary.Dispose(); // twice is fine

        using var same = ZstandardDictionary.Create(content);
        var buffer = new byte[64 * 1024];
        foreach (var encoder in encoders)
        {
            Assert.Equal(OperationStatus.Done, encoder.Compress(data, buffer, out _, out var written, isFinalBlock: true));
            Assert.Equal(data, Zstandard.Decompress(buffer.AsSpan(0, written), ZstandardDecompressionOptions.Default with { Dictionary = same }));

            // every way of letting go of the dictionary
            encoder.Reset();
            encoder.SetPrefix(Random(1024, 74));
            encoder.Reset(ZstandardCompressionOptions.Default);
            encoder.Dispose();
            encoder.Dispose();
        }
    }

    // ---- a source or destination that fails must end the operation with that failure

    sealed class FailureCounter(int failAtCall)
    {
        int calls;
        public bool ShouldFail() => Interlocked.Increment(ref calls) == failAtCall;
    }

    sealed class FailingMemoryManager(byte[] data, int offset, int length, FailureCounter counter) : MemoryManager<byte>
    {
        public Memory<byte> CreateMemory() => CreateMemory(length);

        public override Span<byte> GetSpan()
        {
            if (counter.ShouldFail()) throw new IOException("source is broken");
            return data.AsSpan(offset, length);
        }

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
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

    static ReadOnlySequence<byte> FailingSequence(byte[] data, int segmentSize, int failAtCall)
    {
        var counter = new FailureCounter(failAtCall);
        Segment? first = null;
        Segment? last = null;
        for (var offset = 0; offset < data.Length; offset += segmentSize)
        {
            var length = Math.Min(segmentSize, data.Length - offset);
            var memory = new FailingMemoryManager(data, offset, length, counter).CreateMemory();
            var segment = new Segment(memory, offset);
            first ??= segment;
            last?.SetNext(segment);
            last = segment;
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    [Theory]
    [InlineData(64 * 1024, 1)]
    [InlineData(64 * 1024, 2)]
    [InlineData(64 * 1024, 7)]
    [InlineData(16 * 1024, 5)] // blocks span segments
    [InlineData(4 * 1024 * 1024, 1)] // one segment
    public async Task LZ4_CompressAsync_Sequence_SourceFailure_Ends(int segmentSize, int failAtCall)
    {
        var data = Compressible(4 * 1024 * 1024, 78);
        var source = FailingSequence(data, segmentSize, failAtCall);
        var options = LZ4CompressionOptions.Default with { BlockSizeID = BlockSizeId.Max64KB };

        var output = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        var compressing = LZ4.CompressAsync(source, output.Writer, options).AsTask();

        await Assert.ThrowsAsync<IOException>(() => compressing.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public async Task LZ4_CompressAsync_Memory_SourceFailure_Ends(int failAtCall)
    {
        var data = Compressible(4 * 1024 * 1024, 79);
        var source = new FailingMemoryManager(data, 0, data.Length, new FailureCounter(failAtCall)).CreateMemory();
        var options = LZ4CompressionOptions.Default with { BlockSizeID = BlockSizeId.Max64KB };

        var output = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        var compressing = LZ4.CompressAsync((ReadOnlyMemory<byte>)source, output.Writer, options).AsTask();

        await Assert.ThrowsAsync<IOException>(() => compressing.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task LZ4_CompressAsync_File_DestinationFailure_Ends(int failAtWrite)
    {
        var path = Path.Combine(tempDir, $"source-{failAtWrite}.bin");
        File.WriteAllBytes(path, Compressible(4 * 1024 * 1024, 80));
        var options = LZ4CompressionOptions.Default with { BlockSizeID = BlockSizeId.Max64KB };

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
        var writer = new FailingWritePipeWriter(failAtWrite);
        var compressing = LZ4.CompressAsync(file.SafeFileHandle, writer, options).AsTask();

        await Assert.ThrowsAsync<IOException>(() => compressing.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    // Holds the first body write until the operation is cancelled, and makes the cancellation itself slow.
    sealed class StallingPipeWriter : PipeWriter
    {
        byte[] current = [];

        public override Memory<byte> GetMemory(int sizeHint = 0) => current = new byte[Math.Max(sizeHint, 1)];
        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public override void Advance(int bytes) { }
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null) { }
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => new(new FlushResult(false, false));

        public override async ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            cancellationToken.Register(() => Thread.Sleep(1000));
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new FlushResult(false, false);
        }
    }

    [Theory]
    [InlineData("sequence", 4)]
    [InlineData("sequence", 5)]
    [InlineData("sequence", 6)]
    [InlineData("sequence", 8)]
    [InlineData("memory", 4)]
    public async Task LZ4_CompressAsync_SourceFailure_ReportsItsException(string source, int failAtCall)
    {
        var data = Compressible(4 * 1024 * 1024, 81);
        var options = LZ4CompressionOptions.Default with { BlockSizeID = BlockSizeId.Max64KB };
        var writer = new StallingPipeWriter();

        var compressing = source == "sequence"
            ? LZ4.CompressAsync(FailingSequence(data, 64 * 1024, failAtCall), writer, options).AsTask()
            : LZ4.CompressAsync((ReadOnlyMemory<byte>)new FailingMemoryManager(data, 0, data.Length, new FailureCounter(failAtCall)).CreateMemory(), writer, options).AsTask();

        await Assert.ThrowsAsync<IOException>(() => compressing.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    // fails the given body write or flush
    sealed class FailingWritePipeWriter(int failAt) : PipeWriter
    {
        byte[] current = [];
        int operations;

        public override Memory<byte> GetMemory(int sizeHint = 0) => current = new byte[Math.Max(sizeHint, 1)];
        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public override void Advance(int bytes) { }
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null) { }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref operations) > failAt) throw new IOException("destination is broken");
            return new(new FlushResult(false, false));
        }

        public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref operations) > failAt) throw new IOException("destination is broken");
            return new(new FlushResult(false, false));
        }
    }

    // ---- 3. compression of a file that shrinks must fail

    sealed class TruncatingPipeWriter(FileStream file, long length) : PipeWriter
    {
        readonly MemoryStream written = new();
        byte[] current = [];
        int flushes;

        public byte[] ToArray()
        {
            lock (written) return written.ToArray();
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => current = new byte[Math.Max(sizeHint, 1)];
        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public override void Advance(int bytes)
        {
            lock (written) written.Write(current, 0, bytes);
        }
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null) { }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            // the header is written before any block is read
            if (Interlocked.Increment(ref flushes) == 1)
            {
                file.SetLength(length);
                file.Flush(flushToDisk: true);
            }
            return new(new FlushResult(false, false));
        }
    }

    [Fact]
    public async Task LZ4_CompressFile_FileShrinks_NeverSucceedsWithBrokenFrame()
    {
        var content = Random(2 * 1024 * 1024, 76);
        var path = Path.Combine(tempDir, "shrinking.bin");
        File.WriteAllBytes(path, content);

        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.Asynchronous);
        var writer = new TruncatingPipeWriter(file, 1024);

        try
        {
            await LZ4.CompressAsync(file.SafeFileHandle, writer).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (LZ4Exception)
        {
            // the length was taken at the start and written to the header, the shorter file cannot satisfy it
            return;
        }

        // The netstandard builds read the handle as a stream of unknown length. They compress what they could read,
        // which includes what was read before the file shrank, and the result is a correct frame.
        var decoded = LZ4.Decompress(writer.ToArray());
        Assert.InRange(decoded.Length, 1024, content.Length - 1);
        Assert.Equal(content.AsSpan(0, decoded.Length).ToArray(), decoded);
    }

    // ---- 4. decoded data goes out before the decoder waits for more input

    [Fact]
    public async Task LZ4_DecompressAsync_DeliversFlushedBlockOfOpenFrame()
    {
        var text = Utf8("hello");
        using var encoder = new LZ4Encoder(LZ4CompressionOptions.Default with { AutoFlush = true, BlockMode = BlockMode.BlockIndependent });
        var buffer = new byte[encoder.GetMaxCompressedLength(text.Length)];

        var input = new Pipe();
        var output = new Pipe();
        var written = encoder.Compress(text, buffer); // header and one block, the frame stays open
        await input.Writer.WriteAsync(buffer.AsMemory(0, written));

        var decompressing = LZ4.DecompressAsync(input.Reader, output.Writer).AsTask();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new MemoryStream();
        while (received.Length < text.Length)
        {
            var result = await output.Reader.ReadAsync(timeout.Token);
            foreach (var segment in result.Buffer) received.Write(segment.ToArray(), 0, segment.Length);
            output.Reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) break;
        }
        Assert.Equal(text, received.ToArray());

        written = encoder.Close(buffer);
        await input.Writer.WriteAsync(buffer.AsMemory(0, written));
        await input.Writer.CompleteAsync();
        await decompressing.WaitAsync(TimeSpan.FromSeconds(10));
        await output.Writer.CompleteAsync();
    }

    // ---- 5. a read cancelled with CancelPendingRead is not input

    sealed class CancellingPipeReader(PipeReader inner, int cancelAtRead) : PipeReader
    {
        int reads;

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref reads) == cancelAtRead)
            {
                inner.CancelPendingRead();
            }
            return await inner.ReadAsync(cancellationToken);
        }

        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
    }

    [Fact]
    public async Task LZ4_DecompressAsync_CancelPendingRead_Throws()
    {
        var data = Compressible(300_000, 77);
        var compressed = LZ4.Compress(data, LZ4CompressionOptions.Default with
        {
            BlockMode = BlockMode.BlockIndependent,
            BlockSizeID = BlockSizeId.Max64KB,
            ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled,
        });

        var cancelled = 0;
        for (var cancelAt = 1; cancelAt <= 12; cancelAt++)
        {
            var input = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            await input.Writer.WriteAsync(compressed);
            await input.Writer.CompleteAsync();

            var reader = new CancellingPipeReader(input.Reader, cancelAt);
            try
            {
                var result = await Collect(w => LZ4.DecompressAsync(reader, w)).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(data, result); // the frame needed fewer reads than that
            }
            catch (OperationCanceledException)
            {
                cancelled++;
            }
            catch (TimeoutException)
            {
                Assert.Fail($"no result when read {cancelAt} is cancelled");
            }
        }

        // the whole frame is available, so it may arrive in a single read
        Assert.True(cancelled >= 1, $"only {cancelled} of the cancelled reads were reported");
    }

    // ---- 6. a FileStream positioned past its end is an empty source

    [Fact]
    public async Task FileStreamPosition_PastEnd_IsEof()
    {
        var path = Path.Combine(tempDir, "three.bin");
        File.WriteAllBytes(path, [1, 2, 3]);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        fs.Position = 20; // legal, a normal ReadByte returns -1 here

        Assert.Empty(LZ4.Decompress(await Collect(w => LZ4.CompressAsync(fs, w))));
        Assert.Equal(20, fs.Position);

        Assert.Empty(Zstandard.Decompress(await Collect(w => Zstandard.CompressAsync(fs, w))));
        Assert.Equal(20, fs.Position);

        Assert.Empty(await Collect(w => LZ4.DecompressAsync(fs, w)));
        Assert.Equal(20, fs.Position);

        Assert.Empty(await Collect(w => Zstandard.DecompressAsync(fs, w)));
        Assert.Equal(20, fs.Position);

        // at the end behaves the same
        fs.Position = 3;
        Assert.Empty(LZ4.Decompress(await Collect(w => LZ4.CompressAsync(fs, w))));
        Assert.Equal(3, fs.Position);
    }
}
