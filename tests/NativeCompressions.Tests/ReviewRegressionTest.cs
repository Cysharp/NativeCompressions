using System.Buffers;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;

namespace NativeCompressions.Tests;

// Regressions from the v1.0 pre-release review. Each test names the review item it covers.
public class ReviewRegressionTest
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    static byte[] Compressible(int size, int seed)
    {
        var rand = new Random(seed);
        var words = new[] { "review ", "native ", "compression ", "dotnet ", "regression ", "\n" };
        var sb = new StringBuilder(size);
        while (sb.Length < size)
        {
            sb.Append(words[rand.Next(words.Length)]);
            if (rand.Next(50) == 0) sb.Append(rand.Next());
        }
        return Encoding.ASCII.GetBytes(sb.ToString(0, size));
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

    // Compress with a prefix in two pieces so the frame really depends on it, with a checksum so a wrong prefix is detected.
    static byte[] ZstdCompressWithPrefix(byte[] data, byte[] prefix)
    {
        using var encoder = new ZstandardEncoder(ZstandardCompressionOptions.Default with { ChecksumFlag = true });
        encoder.SetPrefix(prefix);
        var output = new byte[Zstandard.GetMaxCompressedLength(data.Length) + 64];
        var ms = new MemoryStream();
        var half = data.Length / 2;
        Assert.Equal(OperationStatus.Done, encoder.Compress(data.AsSpan(0, half), output, out _, out var w1, isFinalBlock: false));
        ms.Write(output, 0, w1);
        Assert.Equal(OperationStatus.Done, encoder.Compress(data.AsSpan(half), output, out _, out var w2, isFinalBlock: true));
        ms.Write(output, 0, w2);
        return ms.ToArray();
    }

    // ---- 1. Zstandard Reset must drop the native prefix reference before unpinning

    [Fact]
    public void Zstd_DecoderReset_DropsPrefix()
    {
        var prefix = Random(8192, 1);
        var data = prefix.AsSpan(0, 4096).ToArray().Concat(Utf8("tail")).ToArray(); // heavily references the prefix
        var compressed = ZstdCompressWithPrefix(data, prefix);

        using var decoder = new ZstandardDecoder();
        var dest = new byte[data.Length + 16];

        // sanity: with the prefix it decodes
        decoder.SetPrefix(prefix);
        Assert.Equal(OperationStatus.Done, decoder.Decompress(compressed, dest, out _, out var written));
        Assert.Equal(data, dest.AsSpan(0, written).ToArray());

        // after Reset the prefix is gone, so the frame must not decode as if the prefix were still set
        var volatilePrefix = prefix.ToArray();
        decoder.Reset();
        decoder.SetPrefix(volatilePrefix);
        decoder.Reset();
        Array.Clear(volatilePrefix);
        var status = decoder.Decompress(compressed, dest, out _, out written);
        Assert.NotEqual(OperationStatus.Done, status);
    }

    [Fact]
    public void Zstd_EncoderReset_DropsPrefix()
    {
        var prefix = Random(8192, 2);
        var data = prefix.AsSpan(0, 4096).ToArray().Concat(Utf8("tail")).ToArray();

        using var encoder = new ZstandardEncoder(ZstandardCompressionOptions.Default with { ChecksumFlag = true });
        encoder.SetPrefix(prefix);
        encoder.Reset();

        // no prefix is referenced any more, so a plain decoder must read the frame
        var output = new byte[Zstandard.GetMaxCompressedLength(data.Length) + 64];
        Assert.Equal(OperationStatus.Done, encoder.Compress(data, output, out _, out var written, isFinalBlock: true));
        Assert.Equal(data, Zstandard.Decompress(output.AsSpan(0, written)));
    }

    // ---- 2. a rejected SetPrefix must leave the current prefix in place

    [Fact]
    public void Zstd_SetPrefixFailure_KeepsCurrentPrefix()
    {
        var prefix = Random(8192, 3);
        var data = prefix.AsSpan(0, 4096).ToArray().Concat(Utf8("tail")).ToArray();
        var compressed = ZstdCompressWithPrefix(data, prefix);

        using var decoder = new ZstandardDecoder();
        decoder.SetPrefix(prefix);

        var dest = new byte[data.Length + 16];
        var ms = new MemoryStream();

        // start the frame
        var status = decoder.Decompress(compressed.AsSpan(0, 10), dest, out var consumed, out var written);
        Assert.NotEqual(OperationStatus.InvalidData, status);
        ms.Write(dest, 0, written);

        // mid frame the prefix cannot be changed
        var candidate = Random(8192, 4);
        Assert.Throws<ZstandardException>(() => decoder.SetPrefix(candidate));
        Array.Clear(candidate);

        // the original prefix is still in effect and the frame completes correctly
        var remaining = compressed.AsSpan(consumed);
        while (true)
        {
            status = decoder.Decompress(remaining, dest, out consumed, out written);
            Assert.NotEqual(OperationStatus.InvalidData, status);
            ms.Write(dest, 0, written);
            remaining = remaining.Slice(consumed);
            if (status == OperationStatus.Done) break;
        }
        Assert.Equal(data, ms.ToArray());

        // encoder side: a rejected SetPrefix mid frame keeps the frame usable
        using var encoder = new ZstandardEncoder(ZstandardCompressionOptions.Default with { ChecksumFlag = true });
        encoder.SetPrefix(prefix);
        var output = new byte[Zstandard.GetMaxCompressedLength(data.Length) + 64];
        var frame = new MemoryStream();
        Assert.Equal(OperationStatus.Done, encoder.Compress(data.AsSpan(0, 100), output, out _, out var w1, isFinalBlock: false));
        frame.Write(output, 0, w1);
        Assert.Throws<ZstandardException>(() => encoder.SetPrefix(Random(8192, 5)));
        Assert.Equal(OperationStatus.Done, encoder.Compress(data.AsSpan(100), output, out _, out var w2, isFinalBlock: true));
        frame.Write(output, 0, w2);

        using var reader = new ZstandardDecoder();
        reader.SetPrefix(prefix);
        Assert.Equal(OperationStatus.Done, reader.Decompress(frame.ToArray(), dest, out _, out written));
        Assert.Equal(data, dest.AsSpan(0, written).ToArray());
    }

    // ---- 3. a complete frame on a pipe that stays open must be delivered without waiting for more input

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LZ4_PipeReader_DeliversFrameWhileInputStaysOpen(int dop)
    {
        var data = Compressible(200_000, 6);
        var compressed = LZ4.Compress(data, LZ4CompressionOptions.Default with { BlockMode = BlockMode.BlockIndependent, BlockSizeID = BlockSizeId.Max64KB });

        var input = new Pipe();
        var output = new Pipe();
        await input.Writer.WriteAsync(compressed);
        await input.Writer.FlushAsync(); // frame is complete, but the writer is not completed

        var decompressing = LZ4.DecompressAsync(input.Reader, output.Writer, maxDegreeOfParallelism: dop).AsTask();

        // the whole frame must arrive while the input is still open
        var received = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (received.Length < data.Length)
        {
            var result = await output.Reader.ReadAsync(timeout.Token);
            foreach (var segment in result.Buffer) received.Write(segment.Span);
            output.Reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) break;
        }
        Assert.Equal(data, received.ToArray());

        // closing the input ends the decompression
        await input.Writer.CompleteAsync();
        await decompressing.WaitAsync(TimeSpan.FromSeconds(10));
        await output.Writer.CompleteAsync();
    }

    [Fact]
    public async Task Zstd_PipeReader_DeliversFrameWhileInputStaysOpen()
    {
        var data = Compressible(200_000, 7);
        var compressed = Zstandard.Compress(data);

        var input = new Pipe();
        var output = new Pipe();
        await input.Writer.WriteAsync(compressed);
        await input.Writer.FlushAsync();

        var decompressing = Zstandard.DecompressAsync(input.Reader, output.Writer).AsTask();

        var received = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (received.Length < data.Length)
        {
            var result = await output.Reader.ReadAsync(timeout.Token);
            foreach (var segment in result.Buffer) received.Write(segment.Span);
            output.Reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) break;
        }
        Assert.Equal(data, received.ToArray());

        await input.Writer.CompleteAsync();
        await decompressing.WaitAsync(TimeSpan.FromSeconds(10));
        await output.Writer.CompleteAsync();
    }

    // ---- 4. a failing destination must end parallel compression with that failure

    sealed class FailingPipeWriter : PipeWriter
    {
        readonly Pipe pipe = new();
        int flushes;

        public FailingPipeWriter()
        {
            _ = Task.Run(async () =>
            {
                // drain so writes never block on back pressure
                while (true)
                {
                    var result = await pipe.Reader.ReadAsync();
                    pipe.Reader.AdvanceTo(result.Buffer.End);
                    if (result.IsCompleted) break;
                }
            });
        }

        public override void Advance(int bytes) => pipe.Writer.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => pipe.Writer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => pipe.Writer.GetSpan(sizeHint);
        public override void CancelPendingFlush() => pipe.Writer.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => pipe.Writer.Complete(exception);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            // the header flush succeeds, the first body flush fails
            if (Interlocked.Increment(ref flushes) > 1)
            {
                throw new IOException("destination is broken");
            }
            return pipe.Writer.FlushAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task LZ4_ParallelCompress_EndsWhenDestinationFails()
    {
        var data = Compressible(8 * 1024 * 1024, 8);
        var options = LZ4CompressionOptions.Default with { BlockSizeID = BlockSizeId.Max64KB };
        var writer = new FailingPipeWriter();

        var compressing = LZ4.CompressAsync((ReadOnlyMemory<byte>)data, writer, options, maxDegreeOfParallelism: 4).AsTask();
        await Assert.ThrowsAsync<IOException>(() => compressing.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    // ---- 6. ZstandardStream must not drop a frame whose Close failed

    [Fact]
    public void Zstd_Stream_CloseFailure_Throws()
    {
        var ms = new MemoryStream();
        var zs = new ZstandardStream(ms, CompressionMode.Compress, leaveOpen: true);
        zs.SetSourceLength(100);
        zs.Write(new byte[25]);
        Assert.Throws<InvalidOperationException>(() => zs.Dispose());
    }

    [Fact]
    public async Task Zstd_Stream_CloseFailure_ThrowsAsync()
    {
        var ms = new MemoryStream();
        var zs = new ZstandardStream(ms, CompressionMode.Compress, leaveOpen: true);
        zs.SetSourceLength(100);
        await zs.WriteAsync(new byte[25]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await zs.DisposeAsync());
    }

    // ---- 7. LZ4 parallel decode must reject a content size that does not match

    static byte[] LZ4FrameWithWrongContentSize(byte[] body, ulong claimed)
    {
        using var encoder = new LZ4Encoder(LZ4CompressionOptions.Default with
        {
            ContentSize = claimed,
            BlockMode = BlockMode.BlockIndependent,
            BlockSizeID = BlockSizeId.Max64KB,
            AutoFlush = true,
        });
        var buffer = new byte[encoder.GetMaxCompressedLength(body.Length)];
        var ms = new MemoryStream();
        ms.Write(buffer, 0, encoder.Compress(body, buffer)); // header plus flushed blocks
        ms.Write(new byte[4]); // end mark without going through Close, which would refuse the size mismatch
        return ms.ToArray();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task LZ4_ContentSizeMismatch_RejectedInEveryPath(int dop)
    {
        var frame = LZ4FrameWithWrongContentSize(Compressible(100_000, 9), claimed: 200_000);

        Assert.Throws<LZ4Exception>(() => LZ4.Decompress(frame));
        await Assert.ThrowsAsync<LZ4Exception>(async () => await Collect(w => LZ4.DecompressAsync((ReadOnlyMemory<byte>)frame, w, maxDegreeOfParallelism: dop)));
    }

    // ---- 8. LZ4 parallel decode must reject a block larger than the frame's block size

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task LZ4_OversizeBlock_RejectedInEveryPath(int dop)
    {
        var options = LZ4CompressionOptions.Default with { BlockMode = BlockMode.BlockIndependent, BlockSizeID = BlockSizeId.Max64KB };
        var headerOnly = LZ4.Compress(ReadOnlySpan<byte>.Empty, options);
        using var probe = new LZ4Decoder();
        var headerSize = probe.GetHeaderSize(headerOnly);

        // an "uncompressed" block that claims 65,794 bytes, more than the 64KB block size
        const int oversize = 65_794;
        var frame = new MemoryStream();
        frame.Write(headerOnly, 0, headerSize);
        frame.Write(BitConverter.GetBytes(0x80000000u | oversize));
        frame.Write(Random(oversize, 10));
        frame.Write(new byte[4]);
        var bytes = frame.ToArray();

        Assert.Throws<LZ4Exception>(() => LZ4.Decompress(bytes));
        await Assert.ThrowsAsync<LZ4Exception>(async () => await Collect(w => LZ4.DecompressAsync((ReadOnlyMemory<byte>)bytes, w, maxDegreeOfParallelism: dop)));
    }

    // ---- 9. LZ4Encoder.Reset must discard buffered input

    [Fact]
    public void LZ4_EncoderReset_DiscardsBufferedInput()
    {
        using var encoder = new LZ4Encoder();
        var buffer = new byte[encoder.GetMaxCompressedLength(1024)];

        encoder.Compress(Utf8("abandoned-data"), buffer);
        encoder.Reset();
        Assert.Equal(0, encoder.Flush(buffer));

        encoder.Compress(Utf8("abandoned-data"), buffer);
        encoder.Reset(LZ4CompressionOptions.Default with { CompressionLevel = 3 });
        Assert.Equal(0, encoder.Flush(buffer));

        var ms = new MemoryStream();
        ms.Write(buffer, 0, encoder.Compress(Utf8("kept"), buffer));
        ms.Write(buffer, 0, encoder.Close(buffer));
        Assert.Equal(Utf8("kept"), LZ4.Decompress(ms.ToArray()));
    }

    // ---- re-review: a failing worker must end parallel decompression even when the input stays open

    [Fact]
    public async Task LZ4_ParallelDecompress_WorkerFailure_EndsWithOpenInput()
    {
        var data = Compressible(300_000, 12);
        var compressed = LZ4.Compress(data, LZ4CompressionOptions.Default with
        {
            BlockMode = BlockMode.BlockIndependent,
            BlockSizeID = BlockSizeId.Max64KB,
            BlockChecksumFlag = BlockChecksum.BlockChecksumEnabled,
        });

        // header plus the first complete block, with a corrupted payload byte so its checksum fails
        using var probe = new LZ4Decoder();
        var headerSize = probe.GetHeaderSize(compressed);
        var blockSize = (int)(BitConverter.ToUInt32(compressed, headerSize) & 0x7FFFFFFF);
        var firstBlockEnd = headerSize + 4 + blockSize + 4;
        var partial = compressed.AsSpan(0, firstBlockEnd).ToArray();
        partial[headerSize + 4 + blockSize / 2] ^= 0xFF;

        var input = new Pipe();
        await input.Writer.WriteAsync(partial);
        await input.Writer.FlushAsync(); // more input never arrives and the writer stays open

        var output = new Pipe();
        _ = ReadAllAsync(output.Reader);
        var decompressing = LZ4.DecompressAsync(input.Reader, output.Writer, maxDegreeOfParallelism: 4).AsTask();
        await Assert.ThrowsAsync<LZ4Exception>(() => decompressing.WaitAsync(TimeSpan.FromSeconds(10)));
        await output.Writer.CompleteAsync();
    }

    // ---- re-review: a failed Close must still release the inner stream

    [Fact]
    public void Zstd_Stream_CloseFailure_StillDisposesInner()
    {
        var inner = new MemoryStream();
        var zs = new ZstandardStream(inner, CompressionMode.Compress, leaveOpen: false);
        zs.SetSourceLength(100);
        zs.Write(new byte[25]);
        Assert.Throws<InvalidOperationException>(() => zs.Dispose());
        Assert.False(inner.CanWrite);
        zs.Dispose(); // second dispose is a no-op
    }

    [Fact]
    public async Task Zstd_Stream_CloseFailure_StillDisposesInnerAsync()
    {
        var inner = new MemoryStream();
        var zs = new ZstandardStream(inner, CompressionMode.Compress, leaveOpen: false);
        zs.SetSourceLength(100);
        await zs.WriteAsync(new byte[25]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await zs.DisposeAsync());
        Assert.False(inner.CanWrite);
    }

    [Fact]
    public void LZ4_Stream_CloseFailure_StillDisposesInner()
    {
        var inner = new MemoryStream();
        var zs = new LZ4Stream(inner, LZ4CompressionOptions.Default with { ContentSize = 100 }, leaveOpen: false);
        zs.Write(new byte[25]);
        Assert.Throws<LZ4Exception>(() => zs.Dispose());
        Assert.False(inner.CanWrite);
    }

    [Fact]
    public async Task LZ4_Stream_CloseFailure_StillDisposesInnerAsync()
    {
        var inner = new MemoryStream();
        var zs = new LZ4Stream(inner, LZ4CompressionOptions.Default with { ContentSize = 100 }, leaveOpen: false);
        await zs.WriteAsync(new byte[25]);
        await Assert.ThrowsAsync<LZ4Exception>(async () => await zs.DisposeAsync());
        Assert.False(inner.CanWrite);
    }

    // ---- re-review: a MemoryStream position past the end is EOF, not an error

    [Fact]
    public async Task MemoryStreamPosition_PastEnd_IsEof()
    {
        var empty = new MemoryStream();
        empty.Position = 10; // legal, a normal ReadByte returns -1 here

        var lz4 = await Collect(w => LZ4.CompressAsync(empty, w));
        Assert.Empty(LZ4.Decompress(lz4));
        Assert.Equal(10, empty.Position);

        var zstd = await Collect(w => Zstandard.CompressAsync(empty, w));
        Assert.Empty(Zstandard.Decompress(zstd));
        Assert.Equal(10, empty.Position);

        Assert.Empty(await Collect(w => LZ4.DecompressAsync(empty, w)));
        Assert.Equal(10, empty.Position);
        Assert.Empty(await Collect(w => Zstandard.DecompressAsync(empty, w)));
        Assert.Equal(10, empty.Position);

        // a stream with content but positioned at its end behaves the same
        var atEnd = new MemoryStream(Utf8("payload"));
        atEnd.Position = atEnd.Length;
        Assert.Empty(LZ4.Decompress(await Collect(w => LZ4.CompressAsync(atEnd, w))));
        Assert.Equal(atEnd.Length, atEnd.Position);
    }

    // ---- 10. MemoryStream sources must honor Position

    [Fact]
    public async Task MemoryStreamPosition_IsHonored_ByBothAlgorithms()
    {
        var header = Utf8("HEADER");
        var payload = Compressible(50_000, 11);
        var exposed = new MemoryStream(header.Concat(payload).ToArray(), 0, header.Length + payload.Length, writable: false, publiclyVisible: true);

        exposed.Position = header.Length;
        var lz4 = await Collect(w => LZ4.CompressAsync(exposed, w));
        Assert.Equal(payload, LZ4.Decompress(lz4));
        Assert.Equal(exposed.Length, exposed.Position);

        exposed.Position = header.Length;
        var zstd = await Collect(w => Zstandard.CompressAsync(exposed, w));
        Assert.Equal(payload, Zstandard.Decompress(zstd));
        Assert.Equal(exposed.Length, exposed.Position);

        var lz4Source = new MemoryStream(header.Concat(lz4).ToArray(), 0, header.Length + lz4.Length, writable: false, publiclyVisible: true) { Position = header.Length };
        Assert.Equal(payload, await Collect(w => LZ4.DecompressAsync(lz4Source, w)));
        Assert.Equal(lz4Source.Length, lz4Source.Position);

        var zstdSource = new MemoryStream(header.Concat(zstd).ToArray(), 0, header.Length + zstd.Length, writable: false, publiclyVisible: true) { Position = header.Length };
        Assert.Equal(payload, await Collect(w => Zstandard.DecompressAsync(zstdSource, w)));
        Assert.Equal(zstdSource.Length, zstdSource.Position);
    }
}
