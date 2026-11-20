using System.Buffers;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NativeCompressions.Tests;

// Regressions from the security review before MessagePack for C# v4. Each test names the review item it covers.
public class ReviewRegressionTest13
{
    static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    // ---- 1. StableDst is gone. With it lz4frame kept the address of the previous destination between calls, and once
    //         the GC had moved that array the next block copied its matches from whatever was at the old address.

    [Fact]
    public void LZ4DecompressionOptions_HasNoStableDst()
    {
        Assert.Null(typeof(LZ4DecompressionOptions).GetProperty("StableDst"));
    }

    [Fact]
    public void LZ4Decoder_DoesNotReadEarlierDestinations()
    {
        // linked 64KB blocks, every block after the first is made of matches into the one before it
        var period = RandomBytes(60_000, 1);
        var data = new byte[300_000];
        for (var i = 0; i < data.Length; i++) data[i] = period[i % period.Length];
        var compressed = LZ4.Compress(data, LZ4CompressionOptions.Default with { BlockMode = BlockMode.BlockLinked, BlockSizeId = BlockSizeId.Max64KB });

        using var decoder = new LZ4Decoder();
        var output = new MemoryStream();
        ReadOnlySpan<byte> source = compressed;
        while (true)
        {
            // a block per call, each into its own array that is overwritten as soon as its content is taken
            var destination = new byte[65_536];
            var status = decoder.Decompress(source, destination, out var consumed, out var written);
            Assert.NotEqual(OperationStatus.InvalidData, status);
            source = source.Slice(consumed);
            output.Write(destination, 0, written);
            destination.AsSpan().Fill(0x41);

            if (status == OperationStatus.Done) break;
            Assert.True(consumed > 0 || written > 0);
        }

        Assert.True(source.IsEmpty);
        Assert.Equal(data, output.ToArray());
    }

    // ---- 2. the native libraries are not looked up in the current directory and PATH

    [Fact]
    public void CoreAssemblies_RestrictTheNativeLibraryLookup()
    {
        foreach (var assembly in new[] { typeof(LZ4).Assembly, typeof(Zstandard).Assembly })
        {
            var attribute = assembly.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
            Assert.NotNull(attribute);
            Assert.Equal(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories, attribute.Paths);
        }
    }

    // ---- 3. LZ4Encoder refuses an lz4 older than v1.11.0, whose calls fail unless the destination holds their worst case.
    //         The bundled library has to pass.

    [Fact]
    public void BundledLZ4_IsNewEnoughForTheEncoder()
    {
        Assert.True(LZ4.VersionNumber >= 1_11_00, $"lz4 {LZ4.Version}");
        using var encoder = new LZ4Encoder();
    }

    // ---- 4. one asynchronous operation at a time on the streams. A second one used the native context and the buffer
    //         together with the first.

    // Asynchronous reads, writes and flushes wait until Release, so an operation of the outer stream stays in flight.
    sealed class GatedStream(Stream inner) : Stream
    {
        readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => gate.TrySetResult();

        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        // the netstandard2.0 build of the library calls the array overloads, the other builds the memory ones
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await gate.Task;
            return await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.Task;
            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await gate.Task;
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.Task;
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await gate.Task;
            await inner.FlushAsync(cancellationToken);
        }
    }

    static async Task AssertOneAsyncReadAtATime(Func<Stream, Stream> open, byte[] compressed, byte[] data)
    {
        var gated = new GatedStream(new MemoryStream(compressed));
        using var stream = open(gated);

        var buffer = new byte[data.Length];
        var first = stream.ReadAsync(buffer, 0, buffer.Length); // waits for the inner stream
        Assert.False(first.IsCompleted);

        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.ReadAsync(new byte[16], 0, 16));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await stream.ReadAsync(new byte[16].AsMemory()));
        Assert.Throws<InvalidOperationException>(() => stream.Read(new byte[16], 0, 16));
        Assert.Throws<InvalidOperationException>(() => stream.ReadByte());

        // the refused calls took nothing, the first read and the ones after it deliver everything
        gated.Release();
        var read = await first;
        while (read < data.Length)
        {
            var count = await stream.ReadAsync(buffer, read, buffer.Length - read);
            Assert.True(count > 0);
            read += count;
        }
        Assert.Equal(data, buffer);
        Assert.Equal(0, await stream.ReadAsync(new byte[16], 0, 16));
    }

    static async Task AssertOneAsyncWriteAtATime(Func<Stream, Stream> open, Func<byte[], byte[]> decompress, byte[] data)
    {
        var output = new MemoryStream();
        var gated = new GatedStream(output);
        var stream = open(gated);

        // incompressible, so the first write already has blocks to hand to the inner stream
        var first = stream.WriteAsync(data, 0, data.Length);
        Assert.False(first.IsCompleted);

        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.WriteAsync(new byte[16], 0, 16));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await stream.WriteAsync(new byte[16].AsMemory()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.FlushAsync());
        Assert.Throws<InvalidOperationException>(() => stream.Write(new byte[16], 0, 16));
        Assert.Throws<InvalidOperationException>(() => stream.WriteByte(1));
        Assert.Throws<InvalidOperationException>(() => stream.Flush());

        // the refused calls added nothing to the frame
        gated.Release();
        await first;
        await stream.FlushAsync();
        await stream.WriteAsync(data, 0, 1000);
        await stream.DisposeAsync();

        var expected = data.Concat(data.Take(1000)).ToArray();
        Assert.Equal(expected, decompress(output.ToArray()));
    }

    [Fact]
    public Task LZ4Stream_OneAsyncReadAtATime()
    {
        var data = RandomBytes(100_000, 2);
        return AssertOneAsyncReadAtATime(inner => new LZ4Stream(inner, CompressionMode.Decompress), LZ4.Compress(data), data);
    }

    [Fact]
    public Task ZstandardStream_OneAsyncReadAtATime()
    {
        var data = RandomBytes(100_000, 3);
        return AssertOneAsyncReadAtATime(inner => new ZstandardStream(inner, CompressionMode.Decompress), Zstandard.Compress(data), data);
    }

    [Fact]
    public Task LZ4Stream_OneAsyncWriteAtATime()
    {
        return AssertOneAsyncWriteAtATime(inner => new LZ4Stream(inner, CompressionMode.Compress), compressed => LZ4.Decompress(compressed), RandomBytes(400_000, 4));
    }

    [Fact]
    public Task ZstandardStream_OneAsyncWriteAtATime()
    {
        return AssertOneAsyncWriteAtATime(inner => new ZstandardStream(inner, CompressionMode.Compress), compressed => Zstandard.Decompress(compressed), RandomBytes(400_000, 5));
    }
}
