using System.Buffers;
using System.Diagnostics.Tracing;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NativeCompressions.Interop;

namespace NativeCompressions.Tests;

// Regressions from the third v1.0 pre-release review. Each test names the review item it covers.
public class ReviewRegressionTest3
{
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

    // ---- 1. TryGetFrameInfo must work when the out argument lives on the heap

    sealed class FrameInfoHolder
    {
        public LZ4FrameInfo Info;
    }

    [Fact]
    public void LZ4_TryGetFrameInfo_OutOnHeap()
    {
        var data = Random(1024 * 1024, 31);
        var compressed = LZ4.Compress(data, LZ4CompressionOptions.Default with { ContentSize = (ulong)data.Length });

        var holder = new FrameInfoHolder();
        Assert.True(LZ4.TryGetFrameInfo(compressed, out holder.Info));
        Assert.Equal((ulong)data.Length, holder.Info.ContentSize);

        var array = new LZ4FrameInfo[3];
        Assert.True(LZ4.TryGetFrameInfo(compressed, out array[1]));
        Assert.Equal((ulong)data.Length, array[1].ContentSize);
        Assert.Equal(default, array[0]);
        Assert.Equal(default, array[2]);

        // a failure leaves the default value
        holder.Info = array[1];
        Assert.False(LZ4.TryGetFrameInfo(Random(64, 32), out holder.Info));
        Assert.Equal(default, holder.Info);
    }

    // ---- 3. a prefix of an abandoned frame is released by Reset and Dispose

    [Fact]
    public void Zstd_Encoder_ResetAndDisposeInsideFrameWithPrefix()
    {
        var prefixA = Random(64 * 1024, 34);
        var prefixB = Random(64 * 1024, 35);
        var data = prefixB.AsSpan(0, 32 * 1024).ToArray().Concat(Random(4 * 1024 * 1024, 36)).ToArray();
        var options = ZstandardCompressionOptions.Default with { ChecksumFlag = true };
        var buffer = new byte[Zstandard.GetMaxCompressedLength(data.Length) + 64];

        for (var i = 0; i < 3; i++)
        {
            using var encoder = new ZstandardEncoder(options);

            // leave a frame unfinished
            encoder.SetPrefix(prefixA);
            Assert.NotEqual(OperationStatus.InvalidData, encoder.Compress(data, buffer, out _, out _, isFinalBlock: false));
            encoder.Reset();
            GC.Collect();

            // the next frame uses another prefix and must decode with it
            encoder.SetPrefix(prefixB);
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

            using var decoder = new ZstandardDecoder();
            decoder.SetPrefix(prefixB);
            var decoded = new byte[data.Length];
            Assert.Equal(OperationStatus.Done, decoder.Decompress(ms.ToArray(), decoded, out _, out var decodedLength));
            Assert.Equal(data.Length, decodedLength);
            Assert.True(data.AsSpan().SequenceEqual(decoded));

            // and dispose inside a frame again
            encoder.SetPrefix(prefixA);
            Assert.NotEqual(OperationStatus.InvalidData, encoder.Compress(data, buffer, out _, out _, isFinalBlock: false));
        }
    }

    // ---- 4. buffers rented while decoding go back to the pool when the data turns out to be invalid

    sealed class ArrayPoolListener : EventListener
    {
        readonly int threadId = Environment.CurrentManagedThreadId;
        readonly HashSet<int> outstanding = new();
        public int Rented { get; private set; }

        public IReadOnlyCollection<int> Outstanding => outstanding;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Buffers.ArrayPoolEventSource")
            {
                EnableEvents(eventSource, EventLevel.Verbose);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            // events are raised on the thread that rents and returns
            if (Environment.CurrentManagedThreadId != threadId || eventData.Payload == null) return;

            switch (eventData.EventName)
            {
                case "BufferRented":
                    Rented++;
                    outstanding.Add((int)eventData.Payload[0]!);
                    break;
                case "BufferReturned":
                    outstanding.Remove((int)eventData.Payload[0]!);
                    break;
            }
        }
    }

    [Fact]
    public void Decompress_InvalidData_ReturnsRentedBuffers()
    {
        var data = Random(2 * 1024 * 1024, 37);
        var lz4 = LZ4.Compress(data);
        var zstd = Zstandard.Compress(data);
        var truncatedLz4 = lz4.AsSpan(0, lz4.Length - 1).ToArray();
        var truncatedZstd = zstd.AsSpan(0, zstd.Length - 1).ToArray();

        using (var listener = new ArrayPoolListener())
        {
            Assert.Throws<LZ4Exception>(() => LZ4.Decompress(truncatedLz4));
            Assert.True(listener.Rented > 0);
            Assert.Empty(listener.Outstanding);
        }

        using (var listener = new ArrayPoolListener())
        {
            Assert.Throws<ZstandardException>(() => Zstandard.Decompress(truncatedZstd));
            Assert.True(listener.Rented > 0);
            Assert.Empty(listener.Outstanding);
        }

        // the successful path returns them as before
        using (var listener = new ArrayPoolListener())
        {
            Assert.Equal(data, LZ4.Decompress(lz4));
            Assert.Equal(data, Zstandard.Decompress(zstd));
            Assert.Empty(listener.Outstanding);
        }
    }

    // ---- 5. a compression stream that never saw a Write still writes a valid empty frame

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LZ4_Stream_NoWrite_ProducesEmptyFrame(bool async)
    {
        var ms = new MemoryStream();
        var stream = new LZ4Stream(ms, CompressionMode.Compress, leaveOpen: true);
        if (async) await stream.DisposeAsync(); else stream.Dispose();

        var compressed = ms.ToArray();
        Assert.NotEmpty(compressed);
        Assert.True(LZ4.TryGetFrameInfo(compressed, out _));
        Assert.Empty(LZ4.Decompress(compressed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zstd_Stream_NoWrite_ProducesEmptyFrame(bool async)
    {
        var ms = new MemoryStream();
        var stream = new ZstandardStream(ms, CompressionMode.Compress, leaveOpen: true);
        if (async) await stream.DisposeAsync(); else stream.Dispose();

        var compressed = ms.ToArray();
        Assert.NotEmpty(compressed);
        Assert.Empty(Zstandard.Decompress(compressed));
    }

    [Fact]
    public async Task Stream_CopyFromEmptySource_ProducesEmptyFrame()
    {
        var lz4 = new MemoryStream();
        using (var stream = new LZ4Stream(lz4, CompressionMode.Compress, leaveOpen: true))
        {
            new MemoryStream().CopyTo(stream);
        }
        Assert.Empty(LZ4.Decompress(lz4.ToArray()));
        Assert.NotEmpty(lz4.ToArray());

        var zstd = new MemoryStream();
        var zstdStream = new ZstandardStream(zstd, CompressionMode.Compress, leaveOpen: true);
        await new MemoryStream().CopyToAsync(zstdStream, 81920, TestContext.Current.CancellationToken);
        await zstdStream.DisposeAsync();
        Assert.Empty(Zstandard.Decompress(zstd.ToArray()));
        Assert.NotEmpty(zstd.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zstd_Stream_NoWrite_SourceLengthMismatchThrows(bool async)
    {
        var stream = new ZstandardStream(new MemoryStream(), CompressionMode.Compress, leaveOpen: true);
        stream.SetSourceLength(100);

        if (async)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await stream.DisposeAsync());
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => stream.Dispose());
        }
    }

    [Fact]
    public void Stream_DecompressMode_DisposeWritesNothing()
    {
        var lz4 = new MemoryStream();
        new LZ4Stream(lz4, CompressionMode.Decompress, leaveOpen: true).Dispose();
        Assert.Equal(0, lz4.Length);

        var zstd = new MemoryStream();
        new ZstandardStream(zstd, CompressionMode.Decompress, leaveOpen: true).Dispose();
        Assert.Equal(0, zstd.Length);
    }

    // ---- 6. a cancelled flush and a completed reader must not be reported as success

    public static IEnumerable<object[]> PipeOperations()
    {
        foreach (var size in new[] { 0, 1000, 2 * 1024 * 1024 })
        {
            yield return new object[] { "lz4-compress", size };
            yield return new object[] { "lz4-decompress", size };
            yield return new object[] { "zstd-compress", size };
            yield return new object[] { "zstd-decompress", size };
        }
    }

    static ValueTask Run(string operation, int size, PipeWriter writer)
    {
        var data = Random(size, 38);
        return operation switch
        {
            "lz4-compress" => LZ4.CompressAsync((ReadOnlyMemory<byte>)data, writer),
            "lz4-decompress" => LZ4.DecompressAsync((ReadOnlyMemory<byte>)LZ4.Compress(data), writer),
            "zstd-compress" => Zstandard.CompressAsync((ReadOnlyMemory<byte>)data, writer),
            "zstd-decompress" => Zstandard.DecompressAsync((ReadOnlyMemory<byte>)Zstandard.Compress(data), writer),
            _ => throw new ArgumentException(operation),
        };
    }

    [Theory]
    [MemberData(nameof(PipeOperations))]
    public async Task CancelledFlush_Throws(string operation, int size)
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        pipe.Writer.CancelPendingFlush(); // the next flush reports IsCanceled

        // an empty decompression may write nothing, then there is no flush to cancel
        if (size == 0 && operation.Contains("decompress")) return;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Run(operation, size, pipe.Writer).AsTask().WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Theory]
    [MemberData(nameof(PipeOperations))]
    public async Task CompletedReader_Throws(string operation, int size)
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        await pipe.Reader.CompleteAsync();

        if (size == 0 && operation.Contains("decompress")) return;

        await Assert.ThrowsAsync<IOException>(async () => await Run(operation, size, pipe.Writer).AsTask().WaitAsync(TimeSpan.FromSeconds(20)));
    }

    // ---- 7. every public P/Invoke declaration must exist in the bundled library

    [Theory]
    [InlineData(typeof(LZ4NativeMethods), "lz4")]
    [InlineData(typeof(ZstandardNativeMethods), "libzstd")]
    public void PublicInterop_EveryEntryPointIsExported(Type nativeMethods, string libraryName)
    {
        var library = NativeMethodsLoader.Load(libraryName);
        Assert.NotEqual(IntPtr.Zero, library);

        var missing = new List<string>();
        var count = 0;
        foreach (var method in nativeMethods.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            var import = method.GetCustomAttribute<DllImportAttribute>();
            if (import == null) continue;

            count++;
            var entryPoint = import.EntryPoint ?? method.Name;
            if (!NativeLibrary.TryGetExport(library, entryPoint, out _))
            {
                missing.Add(entryPoint);
            }
        }

        Assert.True(count > 50, $"only {count} declarations found");
        Assert.True(missing.Count == 0, "not exported: " + string.Join(", ", missing));
    }
}
