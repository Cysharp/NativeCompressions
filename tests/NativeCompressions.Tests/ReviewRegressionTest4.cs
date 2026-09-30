using System.Buffers;
using System.Diagnostics.Tracing;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Runtime.InteropServices;

namespace NativeCompressions.Tests;

// The pool test counts every rent of the process, so nothing else may run next to this class.
[CollectionDefinition(nameof(ReviewRegressionTest4), DisableParallelization = true)]
public class ReviewRegressionTest4Collection
{
}

// Regressions from the recheck of the third v1.0 pre-release review. Each test names the review item it covers.
[Collection(nameof(ReviewRegressionTest4))]
public class ReviewRegressionTest4
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

    static byte[] CompressAll(ZstandardEncoder encoder, byte[] data)
    {
        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();
        var source = data.AsSpan();
        while (true)
        {
            var status = encoder.Compress(source, buffer, out var consumed, out var written, isFinalBlock: true);
            Assert.NotEqual(OperationStatus.InvalidData, status);
            ms.Write(buffer, 0, written);
            source = source.Slice(consumed);
            if (status == OperationStatus.Done) break;
        }
        return ms.ToArray();
    }

    static void StartFrame(ZstandardEncoder encoder, byte[] data)
    {
        var buffer = new byte[Zstandard.GetMaxCompressedLength(data.Length) + 64];
        Assert.NotEqual(OperationStatus.InvalidData, encoder.Compress(data, buffer, out _, out _, isFinalBlock: false));
    }

    // ---- 1. resetting an unfinished multithreaded frame must stop its workers before the prefix is released

    sealed class ObservedMemoryManager(byte[] data) : MemoryManager<byte>
    {
        GCHandle handle;
        public int Pinned { get; private set; }
        public int Unpinned { get; private set; }

        public override Span<byte> GetSpan() => data;

        public override unsafe MemoryHandle Pin(int elementIndex = 0)
        {
            Pinned++;
            handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            return new MemoryHandle((byte*)handle.AddrOfPinnedObject() + elementIndex, default, this);
        }

        public override void Unpin()
        {
            Unpinned++;
            handle.Free();
        }

        protected override void Dispose(bool disposing) { }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Zstd_ResetInsideFrame_ThenSmallFrame(int workers)
    {
        var options = ZstandardCompressionOptions.Default with { NbWorkers = workers, ChecksumFlag = true, CompressionLevel = 5 };
        var big = Compressible(8 * 1024 * 1024, 41);
        var prefix = new ObservedMemoryManager(Random(64 * 1024, 42));

        using var encoder = new ZstandardEncoder(options);
        encoder.SetPrefix(prefix.Memory);
        StartFrame(encoder, big);
        Assert.Equal(0, prefix.Unpinned);

        // with workers the native context is replaced here, which waits for them
        encoder.Reset();
        Assert.Equal(1, prefix.Unpinned);

        // a small frame is compressed without workers and would not wait for the old ones
        var empty = new byte[64];
        Assert.Equal(OperationStatus.Done, encoder.Close(empty, out var written));
        Assert.Empty(Zstandard.Decompress(empty.AsSpan(0, written)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Zstd_ResetInsideFrame_KeepsParameters(int workers)
    {
        var options = ZstandardCompressionOptions.Default with { NbWorkers = workers, ChecksumFlag = true, CompressionLevel = 7, ContentSizeFlag = false };
        var big = Compressible(8 * 1024 * 1024, 43);
        var data = Compressible(3 * 1024 * 1024, 44);

        byte[] expected;
        using (var fresh = new ZstandardEncoder(options))
        {
            expected = CompressAll(fresh, data);
        }

        using var encoder = new ZstandardEncoder(options);
        StartFrame(encoder, big);
        encoder.Reset();

        var actual = CompressAll(encoder, data);
        Assert.True(expected.AsSpan().SequenceEqual(actual));
        Assert.Equal(data, Zstandard.Decompress(actual));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Zstd_ResetInsideFrame_KeepsDictionary(int workers)
    {
        var dictionaryContent = Random(32 * 1024, 45);
        using var dictionary = ZstandardDictionary.Create(dictionaryContent);
        var options = ZstandardCompressionOptions.Default with { NbWorkers = workers, Dictionary = dictionary };
        var big = Compressible(8 * 1024 * 1024, 46);
        var data = dictionaryContent.AsSpan(0, 16 * 1024).ToArray().Concat(Random(1024, 47)).ToArray(); // mostly found in the dictionary

        byte[] expected;
        using (var fresh = new ZstandardEncoder(options))
        {
            expected = CompressAll(fresh, data);
        }

        using var encoder = new ZstandardEncoder(options);
        StartFrame(encoder, big);
        encoder.Reset();

        var actual = CompressAll(encoder, data);
        Assert.True(expected.AsSpan().SequenceEqual(actual));
        Assert.True(actual.Length < data.Length / 4);
        Assert.Equal(data, Zstandard.Decompress(actual, ZstandardDecompressionOptions.Default with { Dictionary = dictionary }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Zstd_ResetInsideFrame_PrefixHadReplacedDictionary(int workers)
    {
        using var dictionary = ZstandardDictionary.Create(Random(32 * 1024, 48));
        var options = ZstandardCompressionOptions.Default with { NbWorkers = workers, Dictionary = dictionary };
        var big = Compressible(8 * 1024 * 1024, 49);
        var data = Compressible(100_000, 50);

        using var encoder = new ZstandardEncoder(options);
        encoder.SetPrefix(Random(1024, 51));
        StartFrame(encoder, big);
        encoder.Reset();

        // neither the prefix nor the dictionary is in effect, a plain decoder reads the frame
        Assert.Equal(data, Zstandard.Decompress(CompressAll(encoder, data)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Zstd_ResetWithOptionsInsideFrame_AppliesNewOptions(int workers)
    {
        var first = ZstandardCompressionOptions.Default with { NbWorkers = workers, CompressionLevel = 1 };
        var second = ZstandardCompressionOptions.Default with { NbWorkers = workers, CompressionLevel = 9, ChecksumFlag = true };
        var big = Compressible(8 * 1024 * 1024, 52);
        var data = Compressible(3 * 1024 * 1024, 53);

        byte[] expected;
        using (var fresh = new ZstandardEncoder(second))
        {
            expected = CompressAll(fresh, data);
        }

        using var encoder = new ZstandardEncoder(first);
        StartFrame(encoder, big);
        encoder.Reset(second);

        var actual = CompressAll(encoder, data);
        Assert.True(expected.AsSpan().SequenceEqual(actual));

        // and the new options survive a later reset inside a frame
        StartFrame(encoder, big);
        encoder.Reset();
        Assert.True(expected.AsSpan().SequenceEqual(CompressAll(encoder, data)));
    }

    // ---- 3. buffers rented by the library go back to the pool when the destination fails

    sealed class ArrayPoolListener : EventListener
    {
        readonly object gate = new();
        readonly Dictionary<int, int> outstanding = new(); // buffer id, size
        int rented;

        // smaller arrays are rented by the runtime all the time
        public int MinimumSize { get; set; } = 64 * 1024;

        public int Rented { get { lock (gate) return rented; } }

        public int[] OutstandingSizes { get { lock (gate) return outstanding.Values.ToArray(); } }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Buffers.ArrayPoolEventSource")
            {
                EnableEvents(eventSource, EventLevel.Verbose);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.Payload == null) return;

            lock (gate)
            {
                switch (eventData.EventName)
                {
                    case "BufferRented":
                        var size = (int)eventData.Payload[1]!;
                        if (size >= MinimumSize)
                        {
                            rented++;
                            outstanding[(int)eventData.Payload[0]!] = size;
                        }
                        break;
                    case "BufferReturned":
                        outstanding.Remove((int)eventData.Payload[0]!);
                        break;
                }
            }
        }
    }

    // Accepts the header, fails the body.
    sealed class BrokenBodyPipeWriter(bool cancel, bool failFlush = false) : PipeWriter
    {
        byte[] current = [];
        int flushes;

        public override Memory<byte> GetMemory(int sizeHint = 0) => current = new byte[Math.Max(sizeHint, 1)];
        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public override void Advance(int bytes) { }
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null) { }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (failFlush && ++flushes > 2) throw new IOException("destination is broken");
            return new(new FlushResult(false, false));
        }

        public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            if (cancel) return new(new FlushResult(isCanceled: true, isCompleted: false));
            throw new IOException("destination is broken");
        }
    }

    // ---- recheck 2, 1. a failed Reset(options) must leave the parameters it had before

    [Fact]
    public void Zstd_FailedResetWithOptions_KeepsPreviousParameters()
    {
        var data = Compressible(3 * 1024 * 1024, 56);
        var disposed = ZstandardDictionary.Create(Random(1024, 57));
        disposed.Dispose();

        byte[] expected;
        using (var fresh = new ZstandardEncoder())
        {
            expected = CompressAll(fresh, data);
        }

        using var encoder = new ZstandardEncoder();

        // the level and the workers are applied before the dictionary is rejected
        var broken = ZstandardCompressionOptions.Default with { CompressionLevel = 19, NbWorkers = 1, Dictionary = disposed };
        Assert.ThrowsAny<Exception>(() => encoder.Reset(broken));

        encoder.Reset();
        Assert.True(expected.AsSpan().SequenceEqual(CompressAll(encoder, data)));

        // an unfinished frame after that has no workers to wait for, and none are running
        var prefix = new ObservedMemoryManager(Random(64 * 1024, 58));
        encoder.SetPrefix(prefix.Memory);
        StartFrame(encoder, Compressible(8 * 1024 * 1024, 59));
        encoder.Reset();
        Assert.Equal(1, prefix.Unpinned);
        Assert.True(expected.AsSpan().SequenceEqual(CompressAll(encoder, data)));
    }

    [Fact]
    public void Zstd_FailedResetWithOptionsInsideFrame_ChangesNothing()
    {
        var options = ZstandardCompressionOptions.Default with { NbWorkers = 2, CompressionLevel = 5 };
        var data = Compressible(3 * 1024 * 1024, 60);
        var disposed = ZstandardDictionary.Create(Random(1024, 61));
        disposed.Dispose();

        byte[] expected;
        using (var fresh = new ZstandardEncoder(options))
        {
            expected = CompressAll(fresh, data);
        }

        using var encoder = new ZstandardEncoder(options);
        StartFrame(encoder, Compressible(8 * 1024 * 1024, 62));
        Assert.ThrowsAny<Exception>(() => encoder.Reset(ZstandardCompressionOptions.Default with { CompressionLevel = 19, Dictionary = disposed }));

        encoder.Reset();
        Assert.True(expected.AsSpan().SequenceEqual(CompressAll(encoder, data)));
    }

    // ---- recheck 2, 2. a dictionary replaced by a prefix may be disposed by its owner

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Zstd_ResetInsideFrame_DictionaryDisposedAfterSetPrefix(int workers)
    {
        var dictionary = ZstandardDictionary.Create(Random(32 * 1024, 63));
        var options = ZstandardCompressionOptions.Default with { NbWorkers = workers, Dictionary = dictionary };
        var data = Compressible(100_000, 64);

        using var encoder = new ZstandardEncoder(options);
        encoder.SetPrefix(Random(1024, 65));
        dictionary.Dispose();

        StartFrame(encoder, Compressible(8 * 1024 * 1024, 66));
        encoder.Reset();
        Assert.Equal(data, Zstandard.Decompress(CompressAll(encoder, data)));

        StartFrame(encoder, Compressible(8 * 1024 * 1024, 67));
        encoder.Reset();
        Assert.Equal(data, Zstandard.Decompress(CompressAll(encoder, data)));
    }

    // ---- recheck 2, 3. readers and writers created by the library give their buffers back on every path

    sealed class ForwardOnlyStream(byte[] data) : Stream
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
            var n = Math.Min(count, data.Length - position);
            Array.Copy(data, position, buffer, offset, n);
            position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class DiscardingPipeWriter : PipeWriter
    {
        byte[] current = [];

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (current.Length < sizeHint || current.Length == 0) current = new byte[Math.Max(sizeHint, 4096)];
            return current;
        }
        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public override void Advance(int bytes) { }
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null) { }
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => new(new FlushResult(false, false));
    }

    [Theory]
    [InlineData("lz4", "intact")]
    [InlineData("lz4", "truncated")]
    [InlineData("lz4", "corrupt")]
    [InlineData("zstd", "intact")]
    [InlineData("zstd", "truncated")]
    [InlineData("zstd", "corrupt")]
    public async Task DecompressFromStream_ReturnsReaderBuffers(string codec, string input)
    {
        var data = Compressible(2 * 1024 * 1024, 68);
        var compressed = codec == "lz4"
            ? LZ4.Compress(data, LZ4CompressionOptions.Default with { BlockChecksumFlag = BlockChecksum.BlockChecksumEnabled })
            : Zstandard.Compress(data, ZstandardCompressionOptions.Default with { ChecksumFlag = true });
        var truncated = input != "intact";
        if (input == "truncated")
        {
            compressed = compressed.AsSpan(0, compressed.Length - 1).ToArray();
        }
        else if (input == "corrupt")
        {
            // fails in the middle, while the reader still holds input that was not consumed
            for (var i = 0; i < 64; i++) compressed[compressed.Length / 3 + i] ^= 0xFF;
        }

        using var listener = new ArrayPoolListener { MinimumSize = 4096 };
        var writer = new DiscardingPipeWriter();
        var operation = codec == "lz4"
            ? LZ4.DecompressAsync(new ForwardOnlyStream(compressed), writer).AsTask()
            : Zstandard.DecompressAsync(new ForwardOnlyStream(compressed), writer).AsTask();

        if (truncated)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => operation.WaitAsync(TimeSpan.FromSeconds(20)));
        }
        else
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(20));
        }

        Assert.True(listener.Rented > 0);
        Assert.Empty(listener.OutstandingSizes);
    }

    [Theory]
    [InlineData("lz4")]
    [InlineData("zstd")]
    public async Task CompressFromStream_DestinationFailure_ReturnsReaderBuffers(string codec)
    {
        var data = Compressible(2 * 1024 * 1024, 69);

        using var listener = new ArrayPoolListener { MinimumSize = 4096 };
        var writer = new BrokenBodyPipeWriter(cancel: false, failFlush: true);
        var operation = codec == "lz4"
            ? LZ4.CompressAsync(new ForwardOnlyStream(data), writer).AsTask()
            : Zstandard.CompressAsync(new ForwardOnlyStream(data), writer).AsTask();

        await Assert.ThrowsAsync<IOException>(() => operation.WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.True(listener.Rented > 0);
        Assert.Empty(listener.OutstandingSizes);
    }

    [Theory]
    [InlineData("lz4")]
    [InlineData("zstd")]
    public async Task FileToFile_ReturnsReaderAndWriterBuffers(string codec)
    {
        var directory = Path.Combine(Path.GetTempPath(), "NativeCompressions.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var data = Compressible(2 * 1024 * 1024, 70);
            var plain = Path.Combine(directory, "plain.bin");
            var compressed = Path.Combine(directory, "compressed.bin");
            var restored = Path.Combine(directory, "restored.bin");
            File.WriteAllBytes(plain, data);

            using (var listener = new ArrayPoolListener { MinimumSize = 4096 })
            {
                if (codec == "lz4")
                {
                    await LZ4.CompressAsync(plain, compressed);
                    await LZ4.DecompressAsync(compressed, restored);
                }
                else
                {
                    await Zstandard.CompressAsync(plain, compressed);
                    await Zstandard.DecompressAsync(compressed, restored);
                }

                Assert.True(listener.Rented > 0);
                Assert.Empty(listener.OutstandingSizes);
            }

            Assert.Equal(data, File.ReadAllBytes(restored));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    // ---- 4. the declared length is checked wherever it was declared

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zstd_Stream_ExternalEncoder_DeclaredLengthWithoutWriteThrows(bool async)
    {
        using var encoder = new ZstandardEncoder();
        encoder.SetSourceLength(100);

        var output = new MemoryStream();
        var stream = new ZstandardStream(output, encoder, leaveOpen: true);

        if (async)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await stream.DisposeAsync());
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => stream.Dispose());
        }
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Zstd_Encoder_DeclaredLength_CheckedWhenFrameStartsAndEndsAtOnce()
    {
        var buffer = new byte[1024];
        using var encoder = new ZstandardEncoder();

        encoder.SetSourceLength(100);
        Assert.Equal(OperationStatus.InvalidData, encoder.Close(buffer, out _));

        encoder.Reset();
        encoder.SetSourceLength(100);
        Assert.Equal(OperationStatus.InvalidData, encoder.Compress(new byte[50], buffer, out _, out _, isFinalBlock: true));

        encoder.Reset();
        encoder.SetSourceLength(100);
        Assert.Equal(OperationStatus.Done, encoder.Compress(new byte[100], buffer, out _, out var written, isFinalBlock: true));
        Assert.Equal(new byte[100], Zstandard.Decompress(buffer.AsSpan(0, written)));

        // the declaration applied to that frame only
        Assert.Equal(OperationStatus.Done, encoder.Close(buffer, out written));
        Assert.Empty(Zstandard.Decompress(buffer.AsSpan(0, written)));

        // a reset drops it
        encoder.SetSourceLength(100);
        encoder.Reset();
        Assert.Equal(OperationStatus.Done, encoder.Close(buffer, out written));
        Assert.Empty(Zstandard.Decompress(buffer.AsSpan(0, written)));

        // zero is a declaration too
        encoder.SetSourceLength(0);
        Assert.Equal(OperationStatus.InvalidData, encoder.Compress(new byte[1], buffer, out _, out _, isFinalBlock: true));
        encoder.Reset();
        encoder.SetSourceLength(0);
        Assert.Equal(OperationStatus.Done, encoder.Close(buffer, out written));
        Assert.Empty(Zstandard.Decompress(buffer.AsSpan(0, written)));
    }
}
