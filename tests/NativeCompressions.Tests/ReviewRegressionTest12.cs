using System.Buffers;
using System.IO.Compression;
using System.IO.Pipelines;

namespace NativeCompressions.Tests;

// Regressions from the line-by-line audit before MessagePack for C# v4. Each test names the audit item it covers.
public class ReviewRegressionTest12
{
    static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    static async Task<byte[]> ToArrayAsync(Func<PipeWriter, ValueTask> write)
    {
        var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
        await write(writer);
        await writer.CompleteAsync();
        return ms.ToArray();
    }

    // ---- 1. LZ4Decoder after InvalidData. lz4frame cannot resume a context after an error, a second call read out of
    //         bounds and later ones copied the source over a native buffer of 64KB.

    // a header with a block checksum, then a one byte block whose checksum is wrong
    static readonly byte[] LZ4BadBlockChecksum = [0x04, 0x22, 0x4D, 0x18, 0x70, 0x40, 0xAD, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    [Fact]
    public void LZ4Decoder_AfterInvalidData_AnswersInvalidDataUntilReset()
    {
        Assert.True(LZ4.TryGetFrameInfo(LZ4BadBlockChecksum, out var info));
        Assert.Equal(BlockChecksum.BlockChecksumEnabled, info.BlockChecksumFlag);

        using var decoder = new LZ4Decoder();
        var destination = new byte[1024];

        var status = decoder.Decompress(LZ4BadBlockChecksum, destination, out var consumed, out var written);
        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(0, consumed);
        Assert.Equal(0, written);

        // more than the native input buffer holds, in pieces and at once
        var large = new byte[200_000];
        large.AsSpan().Fill(0x41);
        for (var i = 0; i < 8; i++)
        {
            status = decoder.Decompress(i % 2 == 0 ? LZ4BadBlockChecksum : large, destination, out consumed, out written);
            Assert.Equal(OperationStatus.InvalidData, status);
            Assert.Equal(0, consumed);
            Assert.Equal(0, written);
        }

        Assert.Throws<LZ4Exception>(() => decoder.GetFrameInfo(LZ4BadBlockChecksum, out _));

        decoder.Reset();
        var data = RandomBytes(3000, 1);
        var frame = LZ4.Compress(data);
        var result = new byte[data.Length];
        Assert.Equal(OperationStatus.Done, decoder.Decompress(frame, result, out consumed, out written));
        Assert.Equal(frame.Length, consumed);
        Assert.Equal(data, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LZ4Stream_AfterInvalidData_KeepsThrowing(bool async)
    {
        var payload = new byte[LZ4BadBlockChecksum.Length + 1024 * 1024];
        payload.AsSpan().Fill(0x41);
        LZ4BadBlockChecksum.CopyTo(payload, 0);

        using var stream = new LZ4Stream(new MemoryStream(payload), CompressionMode.Decompress);
        var buffer = new byte[4096];
        for (var i = 0; i < 8; i++)
        {
            if (async)
            {
                await Assert.ThrowsAsync<LZ4Exception>(async () => await stream.ReadAsync(buffer));
            }
            else
            {
                Assert.Throws<LZ4Exception>(() => stream.Read(buffer, 0, buffer.Length));
            }
        }
    }

    // ---- 2. LZ4Decoder.GetFrameInfo called twice moved the context past the point where the dictionary is installed

    [Fact]
    public void LZ4Decoder_GetFrameInfoTwice_KeepsDictionary()
    {
        var dictionaryBytes = RandomBytes(4096, 2);
        using var dictionary = LZ4Dictionary.Create(dictionaryBytes, 7);
        var data = dictionaryBytes.AsSpan(100, 2000).ToArray(); // only compresses through the dictionary
        var frame = LZ4.Compress(data, LZ4CompressionOptions.Default with { Dictionary = dictionary });
        Assert.True(frame.Length < 200);

        using var decoder = new LZ4Decoder(LZ4DecompressionOptions.Default with { Dictionary = dictionary });
        var first = decoder.GetFrameInfo(frame, out var consumed1);
        var second = decoder.GetFrameInfo(frame.AsSpan(consumed1), out var consumed2);
        Assert.True(consumed1 > 0);
        Assert.Equal(0, consumed2);
        Assert.Equal(first, second);
        Assert.Equal(7u, first.DictionaryId);

        var result = new byte[data.Length];
        Assert.Equal(OperationStatus.Done, decoder.Decompress(frame.AsSpan(consumed1), result, out _, out var written));
        Assert.Equal(data.Length, written);
        Assert.Equal(data, result);

        // the next frame gets its own answer
        var other = LZ4.Compress(data, LZ4CompressionOptions.Default with { ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled });
        var otherInfo = decoder.GetFrameInfo(other, out _);
        Assert.Equal(ContentChecksum.ContentChecksumEnabled, otherInfo.ContentChecksumFlag);
        Assert.Equal(0u, otherInfo.DictionaryId);
    }

    // ---- 3. LZ4.Decompress with trustedData rejected a frame whose end produces no output once the array was full

    [Fact]
    public void LZ4_Decompress_Trusted_EmptyBlockBeforeEndMark()
    {
        var data = RandomBytes(1000, 3);
        var frame = LZ4.Compress(data);
        Assert.True(LZ4.TryGetFrameInfo(frame, out var info));
        Assert.Equal(1000ul, info.ContentSize);
        Assert.Equal(ContentChecksum.NoContentChecksum, info.ContentChecksumFlag);

        // an empty compressed block (one token, no literals) in front of the end mark
        var withEmptyBlock = new byte[frame.Length + 5];
        frame.AsSpan(0, frame.Length - 4).CopyTo(withEmptyBlock);
        new byte[] { 1, 0, 0, 0, 0 }.CopyTo(withEmptyBlock, frame.Length - 4);

        Assert.Equal(data, LZ4.Decompress(withEmptyBlock));
        Assert.Equal(data, LZ4.Decompress(withEmptyBlock, trustedData: true));

        var destination = new byte[data.Length];
        Assert.Equal(data.Length, LZ4.Decompress(withEmptyBlock, destination));

        // a second frame behind it is appended as before
        var two = withEmptyBlock.Concat(frame).ToArray();
        Assert.Equal(data.Concat(data).ToArray(), LZ4.Decompress(two, trustedData: true));

        // input that ends inside the frame is still an error
        var truncated = withEmptyBlock.AsSpan(0, withEmptyBlock.Length - 2).ToArray();
        Assert.Throws<LZ4Exception>(() => LZ4.Decompress(truncated, trustedData: true));
    }

    // ---- 4. an empty slice that does not start at index 0 made the LZ4 async APIs throw ArgumentOutOfRangeException

    public static TheoryData<string> EmptySources => ["memory-empty", "memory-zero", "memory-middle", "memory-end", "sequence-end", "sequence-middle"];

    static (ReadOnlyMemory<byte> Memory, ReadOnlySequence<byte> Sequence, bool IsSequence) EmptySource(string kind)
    {
        var array = new byte[16];
        var sequence = new ReadOnlySequence<byte>(array);
        return kind switch
        {
            "memory-empty" => (ReadOnlyMemory<byte>.Empty, default, false),
            "memory-zero" => (array.AsMemory(0, 0), default, false),
            "memory-middle" => (array.AsMemory(8, 0), default, false),
            "memory-end" => (array.AsMemory(16), default, false),
            "sequence-end" => (default, sequence.Slice(sequence.End), true),
            "sequence-middle" => (default, sequence.Slice(4, 0), true),
            _ => throw new ArgumentException(kind),
        };
    }

    [Theory]
    [MemberData(nameof(EmptySources))]
    public async Task LZ4_CompressAsync_EmptySlice_WritesEmptyFrame(string kind)
    {
        var (memory, sequence, isSequence) = EmptySource(kind);
        var frame = await ToArrayAsync(w => isSequence ? LZ4.CompressAsync(sequence, w) : LZ4.CompressAsync(memory, w));
        Assert.Empty(LZ4.Decompress(frame));
        Assert.True(LZ4.TryGetFrameInfo(frame, out _));
    }

    [Theory]
    [MemberData(nameof(EmptySources))]
    public async Task LZ4_DecompressAsync_EmptySlice_WritesNothing(string kind)
    {
        var (memory, sequence, isSequence) = EmptySource(kind);
        var output = await ToArrayAsync(w => isSequence ? LZ4.DecompressAsync(sequence, w) : LZ4.DecompressAsync(memory, w));
        Assert.Empty(output);
    }

    [Theory]
    [MemberData(nameof(EmptySources))]
    public async Task Zstd_Async_EmptySlice(string kind)
    {
        var (memory, sequence, isSequence) = EmptySource(kind);
        var frame = await ToArrayAsync(w => isSequence ? Zstandard.CompressAsync(sequence, w) : Zstandard.CompressAsync(memory, w));
        Assert.Empty(Zstandard.Decompress(frame));
        var output = await ToArrayAsync(w => isSequence ? Zstandard.DecompressAsync(sequence, w) : Zstandard.DecompressAsync(memory, w));
        Assert.Empty(output);
    }

    // ---- 5. undefined enum values went to lz4 as numbers and gave frames that do not decode

    public static TheoryData<string> UndefinedLZ4Options => ["block-mode", "block-checksum", "content-checksum"];

    static LZ4CompressionOptions Undefined(string which) => which switch
    {
        "block-mode" => LZ4CompressionOptions.Default with { BlockMode = (BlockMode)2 },
        "block-checksum" => LZ4CompressionOptions.Default with { BlockChecksumFlag = (BlockChecksum)2 },
        "content-checksum" => LZ4CompressionOptions.Default with { ContentChecksumFlag = (ContentChecksum)2 },
        _ => throw new ArgumentException(which),
    };

    [Theory]
    [MemberData(nameof(UndefinedLZ4Options))]
    public void LZ4_UndefinedEnumOption_IsRejected(string which)
    {
        var options = Undefined(which);
        var data = RandomBytes(1000, 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => LZ4.Compress(data, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => LZ4.Compress(data, new byte[4096], options));
        Assert.Throws<ArgumentOutOfRangeException>(() => LZ4.GetMaxCompressedLength(1000, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LZ4Encoder(options));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LZ4Stream(new MemoryStream(), options));

        // a failed Reset keeps the previous options and the encoder stays usable
        using var dictionary = LZ4Dictionary.Create(RandomBytes(1024, 5), 9);
        using var encoder = new LZ4Encoder(LZ4CompressionOptions.Default with { Dictionary = dictionary });
        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.Reset(options));

        var destination = new byte[encoder.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, encoder.Compress(data, destination, out _, out var written, isFinalBlock: true));
        Assert.True(LZ4.TryGetFrameInfo(destination.AsSpan(0, written), out var info));
        Assert.Equal(9u, info.DictionaryId);
        Assert.Equal(data, LZ4.Decompress(destination.AsSpan(0, written), LZ4DecompressionOptions.Default with { Dictionary = dictionary }));
    }

    // ---- 6. bound functions answered a size that cannot be compressed with 0, an error code or OverflowException

    [Fact]
    public void LZ4_Block_GetMaxCompressedLength_RejectsSizesABlockCannotHold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LZ4.Block.GetMaxCompressedLength(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LZ4.Block.GetMaxCompressedLength(0x7E000000 + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LZ4.Block.GetMaxCompressedLength(int.MaxValue));

        Assert.Equal(16, LZ4.Block.GetMaxCompressedLength(0));
        Assert.True(LZ4.Block.GetMaxCompressedLength(0x7E000000) > 0x7E000000);
    }

    [Fact]
    public void Zstd_GetMaxCompressedLength_RejectsSizesItCannotBound()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Zstandard.GetMaxCompressedLength(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Zstandard.GetMaxCompressedLength(nuint.MaxValue));

        Assert.True(Zstandard.GetMaxCompressedLength(0) > 0);
        Assert.True(Zstandard.GetMaxCompressedLength((nuint)1000) >= 1000);
    }

    // ---- 7. ZstandardDecoder counted the whole destination as written when a legacy frame failed, and
    //         DecompressAsync handed that memory to the destination. Nothing is counted on InvalidData.

    // the magic of the v0.7 format with a reserved header bit set. A build without legacy support rejects the magic itself.
    static readonly byte[] ZstdBadLegacyFrame = [0x27, 0xB5, 0x2F, 0xFD, 0x08, 0x00];

    [Fact]
    public void ZstdDecoder_InvalidData_CountsNothing_AndStaysInvalidUntilReset()
    {
        using var decoder = new ZstandardDecoder();
        var destination = new byte[1000];
        destination.AsSpan().Fill(0xCC);

        for (var i = 0; i < 4; i++)
        {
            var status = decoder.Decompress(ZstdBadLegacyFrame, destination, out var consumed, out var written);
            Assert.Equal(OperationStatus.InvalidData, status);
            Assert.Equal(0, consumed);
            Assert.Equal(0, written);
        }
        Assert.Equal(-1, destination.AsSpan().IndexOfAnyExcept((byte)0xCC));

        // valid input is not taken either until the decoder is reset
        var data = RandomBytes(3000, 6);
        var frame = Zstandard.Compress(data);
        var result = new byte[data.Length];
        Assert.Equal(OperationStatus.InvalidData, decoder.Decompress(frame, result, out _, out _));

        decoder.Reset();
        Assert.Equal(OperationStatus.Done, decoder.Decompress(frame, result, out var frameConsumed, out var frameWritten));
        Assert.Equal(frame.Length, frameConsumed);
        Assert.Equal(data.Length, frameWritten);
        Assert.Equal(data, result);
    }

    [Fact]
    public async Task Zstd_DecompressAsync_InvalidData_WritesNothing()
    {
        var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
        await Assert.ThrowsAsync<ZstandardException>(async () => await Zstandard.DecompressAsync(ZstdBadLegacyFrame.AsMemory(), writer));
        await writer.CompleteAsync(); // what was advanced is written out here
        Assert.Equal(0, ms.Length);

        var directory = Path.Combine(Path.GetTempPath(), "nc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "in.zst");
            var destination = Path.Combine(directory, "out.bin");
            File.WriteAllBytes(source, ZstdBadLegacyFrame);
            await Assert.ThrowsAsync<ZstandardException>(async () => await Zstandard.DecompressAsync(source, destination));
            Assert.Equal(0, new FileInfo(destination).Length);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    // ---- 8. a failed ZstandardDecoder.Reset(options) left the decoder on default options, without its window limit and dictionary

    [Fact]
    public void ZstdDecoder_FailedReset_KeepsWindowLimit()
    {
        // a streamed frame of 1MB, its window is far above 2^10
        var data = new byte[1 << 20];
        new Random(7).NextBytes(data.AsSpan(0, 4096));
        var ms = new MemoryStream();
        using (var zs = new ZstandardStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            zs.Write(data, 0, data.Length / 2);
            zs.Flush();
            zs.Write(data, data.Length / 2, data.Length / 2);
        }
        var frame = ms.ToArray();
        var destination = new byte[data.Length];

        using var decoder = new ZstandardDecoder(new ZstandardDecompressionOptions { WindowLogMax = 10 });
        Assert.Equal(OperationStatus.InvalidData, decoder.Decompress(frame, destination, out _, out _));
        decoder.Reset();

        Assert.Throws<ZstandardException>(() => decoder.Reset(new ZstandardDecompressionOptions { WindowLogMax = 5 }));

        // the limit of 2^10 is still there
        Assert.Equal(OperationStatus.InvalidData, decoder.Decompress(frame, destination, out _, out _));

        // and options that do apply replace it
        decoder.Reset(ZstandardDecompressionOptions.Default);
        Assert.Equal(OperationStatus.Done, decoder.Decompress(frame, destination, out _, out var written));
        Assert.Equal(data.Length, written);
        Assert.Equal(data, destination);
    }

    [Fact]
    public void ZstdDecoder_FailedReset_KeepsDictionary()
    {
        var dictionaryBytes = RandomBytes(4096, 8);
        using var dictionary = ZstandardDictionary.Create(dictionaryBytes);
        var data = dictionaryBytes.AsSpan(100, 2000).ToArray();
        var frame = Zstandard.Compress(data, new ZstandardCompressionOptions { Dictionary = dictionary });
        var destination = new byte[data.Length];

        using var decoder = new ZstandardDecoder(new ZstandardDecompressionOptions { Dictionary = dictionary });

        var disposed = ZstandardDictionary.Create(dictionaryBytes);
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => decoder.Reset(new ZstandardDecompressionOptions { Dictionary = disposed }));
        Assert.Throws<ZstandardException>(() => decoder.Reset(new ZstandardDecompressionOptions { WindowLogMax = 5 }));

        Assert.Equal(OperationStatus.Done, decoder.Decompress(frame, destination, out _, out var written));
        Assert.Equal(data.Length, written);
        Assert.Equal(data, destination);
    }

    // ---- 9. a frame that is being ended was completed by Flush, and the encoder then took the next frame for a part of it

    [Fact]
    public void ZstdEncoder_EndingFrame_OnlyEndingCanFollow()
    {
        var source = RandomBytes(200, 9);
        var large = new byte[4096];

        using var encoder = new ZstandardEncoder();
        encoder.SetSourceLength(100);
        Assert.Equal(OperationStatus.DestinationTooSmall, encoder.Compress(source.AsSpan(0, 100), new byte[4], out var consumed, out var written, isFinalBlock: true));
        Assert.Equal(100, consumed);
        Assert.Equal(4, written);

        // Flush would complete the frame behind the back of the encoder
        Assert.Equal(OperationStatus.InvalidData, encoder.Flush(large, out written));
        Assert.Equal(0, written);

        // and the encoder stays failed until Reset
        Assert.Equal(OperationStatus.InvalidData, encoder.Close(large, out _));
        Assert.Equal(OperationStatus.InvalidData, encoder.Compress(source, large, out _, out _, isFinalBlock: true));

        encoder.Reset();
        encoder.SetSourceLength(200);
        Assert.Equal(OperationStatus.InvalidData, encoder.Compress(source.AsSpan(0, 150), large, out _, out _, isFinalBlock: true)); // 150 is not the declared 200

        encoder.Reset();
        encoder.SetSourceLength(200);
        Assert.Equal(OperationStatus.Done, encoder.Compress(source, large, out _, out written, isFinalBlock: true));
        Assert.True(Zstandard.TryGetFrameContentSize(large.AsSpan(0, written), out var size));
        Assert.Equal(200ul, size);
        Assert.Equal(source, Zstandard.Decompress(large.AsSpan(0, written)));
    }

    [Theory]
    [InlineData("new-input")]
    [InlineData("non-final")]
    public void ZstdEncoder_EndingFrame_RejectsMoreInput(string what)
    {
        var source = RandomBytes(100, 10);
        using var encoder = new ZstandardEncoder();
        Assert.Equal(OperationStatus.DestinationTooSmall, encoder.Compress(source, new byte[4], out var consumed, out _, isFinalBlock: true));
        Assert.Equal(100, consumed);

        var status = what == "new-input"
            ? encoder.Compress(source, new byte[4096], out _, out _, isFinalBlock: true)
            : encoder.Compress(ReadOnlySpan<byte>.Empty, new byte[4096], out _, out _, isFinalBlock: false);
        Assert.Equal(OperationStatus.InvalidData, status);
    }

    [Fact]
    public void ZstdEncoder_EndingFrame_SmallDestinations_CompleteTheFrame()
    {
        var source = RandomBytes(1000, 11);
        using var encoder = new ZstandardEncoder();
        var output = new MemoryStream();
        var piece = new byte[7];

        // the pattern of BrotliEncoder, the rest of the source and the final flag again until Done
        var remaining = source.AsSpan();
        var status = OperationStatus.DestinationTooSmall;
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = encoder.Compress(remaining, piece, out var consumed, out var written, isFinalBlock: true);
            remaining = remaining.Slice(consumed);
            output.Write(piece, 0, written);
        }
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(source, Zstandard.Decompress(output.ToArray()));

        // and the same through Close
        output.SetLength(0);
        Assert.Equal(OperationStatus.Done, encoder.Compress(source, new byte[4096], out _, out var first, isFinalBlock: false));
        Assert.Equal(0, first); // buffered
        status = OperationStatus.DestinationTooSmall;
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = encoder.Close(piece, out var written);
            output.Write(piece, 0, written);
        }
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(source, Zstandard.Decompress(output.ToArray()));
    }

    // ---- 10. a read that waits for input inside a frame and fails must not count against the decoder.
    //          zstd gives up after 16 calls that make no progress, ZstandardStream then failed for good.

    // Reads what was queued. When nothing is queued it throws, the way a connection does on a timeout.
    sealed class StarvingStream : Stream
    {
        readonly Queue<byte[]> chunks = new();
        public bool Completed;
        public int FailedReads;

        public void Enqueue(byte[] chunk) => chunks.Enqueue(chunk);

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (chunks.Count == 0)
            {
                if (Completed) return 0;
                FailedReads++;
                throw new IOException("no data yet");
            }

            var chunk = chunks.Dequeue();
            Assert.True(chunk.Length <= count);
            chunk.CopyTo(buffer, offset);
            return chunk.Length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try { return Task.FromResult(Read(buffer, offset, count)); }
            catch (Exception ex) { return Task.FromException<int>(ex); }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var array = new byte[buffer.Length];
            try
            {
                var read = Read(array, 0, array.Length);
                array.AsSpan(0, read).CopyTo(buffer.Span);
                return new ValueTask<int>(read);
            }
            catch (Exception ex) { return new ValueTask<int>(Task.FromException<int>(ex)); }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("zstd", false)]
    [InlineData("zstd", true)]
    [InlineData("lz4", false)]
    [InlineData("lz4", true)]
    public async Task Stream_FailedReadsInsideFrame_DoNotBreakTheFrame(string algorithm, bool async)
    {
        var data = RandomBytes(5000, 12);

        // the frame in two parts, the first one flushed with the frame still open
        var compressed = new MemoryStream();
        Stream compressor = algorithm == "zstd"
            ? new ZstandardStream(compressed, CompressionMode.Compress, leaveOpen: true)
            : new LZ4Stream(compressed, CompressionMode.Compress, leaveOpen: true);
        compressor.Write(data, 0, 3000);
        compressor.Flush();
        var part1 = compressed.ToArray();
        compressor.Write(data, 3000, 2000);
        compressor.Dispose();
        var part2 = compressed.ToArray().AsSpan(part1.Length).ToArray();

        var inner = new StarvingStream();
        inner.Enqueue(part1);
        using Stream stream = algorithm == "zstd"
            ? new ZstandardStream(inner, CompressionMode.Decompress)
            : new LZ4Stream(inner, CompressionMode.Decompress);

        var result = new byte[data.Length];
        var total = 0;
        async Task<int> ReadOnce() => async ? await stream.ReadAsync(result.AsMemory(total)) : stream.Read(result, total, result.Length - total);

        while (total < 3000) total += await ReadOnce();
        Assert.Equal(3000, total);

        // far more failed reads than zstd tolerates calls without progress
        for (var i = 0; i < 40; i++)
        {
            await Assert.ThrowsAsync<IOException>(ReadOnce);
        }
        Assert.Equal(40, inner.FailedReads);

        inner.Enqueue(part2);
        inner.Completed = true;
        while (total < data.Length)
        {
            var read = await ReadOnce();
            Assert.True(read > 0);
            total += read;
        }
        Assert.Equal(0, await ReadOnce());
        Assert.Equal(data, result);
    }

    // a decoder handed to a stream while it still holds output of a frame gives that output first
    [Theory]
    [InlineData("zstd")]
    [InlineData("lz4")]
    public void Stream_ExternalDecoderHoldingOutput_OutputComesFirst(string algorithm)
    {
        var data = RandomBytes(3000, 13);
        var result = new byte[data.Length];
        var first = new byte[10];
        int consumed, written;
        Stream stream;
        IDisposable owner;

        if (algorithm == "zstd")
        {
            var frame = Zstandard.Compress(data);
            var decoder = new ZstandardDecoder();
            Assert.Equal(OperationStatus.DestinationTooSmall, decoder.Decompress(frame, first, out consumed, out written));
            stream = new ZstandardStream(new MemoryStream(frame, consumed, frame.Length - consumed), decoder);
            owner = decoder;
        }
        else
        {
            var frame = LZ4.Compress(data);
            var decoder = new LZ4Decoder();
            Assert.Equal(OperationStatus.DestinationTooSmall, decoder.Decompress(frame, first, out consumed, out written));
            stream = new LZ4Stream(new MemoryStream(frame, consumed, frame.Length - consumed), decoder);
            owner = decoder;
        }

        using (owner)
        using (stream)
        {
            Assert.Equal(10, written);
            first.CopyTo(result, 0);
            var total = written;
            int read;
            while ((read = stream.Read(result, total, result.Length - total)) > 0) total += read;
            Assert.Equal(data.Length, total);
            Assert.Equal(data, result);
        }
    }

    // ---- 11. ZstandardStream passed a write of no bytes to the inner stream for every write the encoder only buffered

    sealed class CountingStream : MemoryStream
    {
        public int Writes;
        public int EmptyWrites;

        public override void Write(byte[] buffer, int offset, int count)
        {
            Writes++;
            if (count == 0) EmptyWrites++;
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writes++;
            if (buffer.Length == 0) EmptyWrites++;
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            if (buffer.Length == 0) EmptyWrites++;
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Writes++;
            if (count == 0) EmptyWrites++;
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }

    [Theory]
    [InlineData("zstd", false)]
    [InlineData("zstd", true)]
    [InlineData("lz4", false)]
    [InlineData("lz4", true)]
    public async Task Stream_SmallWrites_NoEmptyWritesOnInnerStream(string algorithm, bool async)
    {
        var inner = new CountingStream();
        var data = RandomBytes(100, 14);
        using (Stream stream = algorithm == "zstd"
            ? new ZstandardStream(inner, CompressionMode.Compress, leaveOpen: true)
            : new LZ4Stream(inner, CompressionMode.Compress, leaveOpen: true))
        {
            for (var i = 0; i < data.Length; i++)
            {
                if (async) await stream.WriteAsync(data.AsMemory(i, 1));
                else stream.WriteByte(data[i]);
            }
            Assert.Equal(0, inner.EmptyWrites);
        }

        Assert.Equal(0, inner.EmptyWrites);
        var frame = inner.ToArray();
        Assert.Equal(data, algorithm == "zstd" ? Zstandard.Decompress(frame) : LZ4.Decompress(frame));
    }

    // ---- 12. Zstandard.CompressAsync recorded the content size for ReadOnlyMemory only, LZ4 records it for every source with a known length

    static readonly byte[] KnownLengthData = RandomBytes(100_000, 15);

    static ulong? ContentSize(byte[] frame) => Zstandard.TryGetFrameContentSize(frame, out var size) ? size : null;

    sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task Zstd_CompressAsync_RecordsKnownLength()
    {
        var data = KnownLengthData;

        var memory = await ToArrayAsync(w => Zstandard.CompressAsync(data.AsMemory(), w));
        Assert.Equal((ulong)data.Length, ContentSize(memory));

        var sequence = await ToArrayAsync(w => Zstandard.CompressAsync(new ReadOnlySequence<byte>(data), w));
        Assert.Equal((ulong)data.Length, ContentSize(sequence));
        Assert.Equal(data, Zstandard.Decompress(sequence));

        // a MemoryStream over an array does not expose its buffer, the length still comes from the stream
        var stream = await ToArrayAsync(w => Zstandard.CompressAsync(new MemoryStream(data), w));
        Assert.Equal((ulong)data.Length, ContentSize(stream));
        Assert.Equal(data, Zstandard.Decompress(stream));

        // from the current position
        var positioned = new MemoryStream(data) { Position = 1000 };
        var fromPosition = await ToArrayAsync(w => Zstandard.CompressAsync(positioned, w));
        Assert.Equal((ulong)(data.Length - 1000), ContentSize(fromPosition));
        Assert.Equal(data.AsSpan(1000).ToArray(), Zstandard.Decompress(fromPosition));

        var exposable = new MemoryStream();
        exposable.Write(data, 0, data.Length);
        exposable.Position = 500;
        var fromExposable = await ToArrayAsync(w => Zstandard.CompressAsync(exposable, w));
        Assert.Equal((ulong)(data.Length - 500), ContentSize(fromExposable));
        Assert.Equal(data.AsSpan(500).ToArray(), Zstandard.Decompress(fromExposable));

        var directory = Path.Combine(Path.GetTempPath(), "nc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "in.bin");
            var destination = Path.Combine(directory, "out.zst");
            File.WriteAllBytes(source, data);

            var fileToWriter = await ToArrayAsync(w => Zstandard.CompressAsync(source, w));
            Assert.Equal((ulong)data.Length, ContentSize(fileToWriter));

            await Zstandard.CompressAsync(source, destination);
            var fileToFile = File.ReadAllBytes(destination);
            Assert.Equal((ulong)data.Length, ContentSize(fileToFile));
            Assert.Equal(data, Zstandard.Decompress(fileToFile));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Zstd_CompressAsync_UnknownLength_RecordsNothing()
    {
        var data = KnownLengthData;

        var nonSeekable = await ToArrayAsync(w => Zstandard.CompressAsync(new NonSeekableStream(data), w));
        Assert.Null(ContentSize(nonSeekable));
        Assert.Equal(data, Zstandard.Decompress(nonSeekable));

        var reader = await ToArrayAsync(w => Zstandard.CompressAsync(PipeReader.Create(new ReadOnlySequence<byte>(data)), w));
        Assert.Null(ContentSize(reader));

        // with an encoder of the caller, declaring a length is left to the caller
        using var encoder = new ZstandardEncoder();
        var withEncoder = await ToArrayAsync(w => Zstandard.CompressAsync(new ReadOnlySequence<byte>(data), w, encoder));
        Assert.Null(ContentSize(withEncoder));

        encoder.SetSourceLength(data.Length);
        var declared = await ToArrayAsync(w => Zstandard.CompressAsync(new ReadOnlySequence<byte>(data), w, encoder));
        Assert.Equal((ulong)data.Length, ContentSize(declared));
    }
}
