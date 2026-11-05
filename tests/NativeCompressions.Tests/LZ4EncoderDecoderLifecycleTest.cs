using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace NativeCompressions.Tests;

// LZ4Encoder / LZ4Decoder / LZ4Dictionary are sealed classes that own a raw native context.
// These tests pin down the ownership semantics and the basic round trips through every path.
public class LZ4EncoderDecoderLifecycleTest
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] Compressible(int size, int seed)
    {
        var rand = new Random(seed);
        var words = new[] { "lz4 ", "native ", "compression ", "dotnet ", "frame ", "\n" };
        var sb = new StringBuilder(size);
        while (sb.Length < size)
        {
            sb.Append(words[rand.Next(words.Length)]);
            if (rand.Next(50) == 0) sb.Append(rand.Next());
        }
        return Encoding.ASCII.GetBytes(sb.ToString(0, size));
    }

    static readonly byte[] Data = Compressible(600 * 1024, seed: 51);

    static byte[] EncodeStreaming(LZ4Encoder encoder, ReadOnlySpan<byte> data, int chunk)
    {
        var ms = new MemoryStream();
        var buffer = new byte[encoder.GetMaxCompressedLength(chunk)];
        var remaining = data;
        while (remaining.Length > 0)
        {
            var piece = remaining.Slice(0, Math.Min(chunk, remaining.Length));
            var written = encoder.CompressDone(piece, buffer);
            ms.Write(buffer, 0, written);
            remaining = remaining.Slice(piece.Length);
        }
        var closed = encoder.CloseDone(buffer);
        ms.Write(buffer, 0, closed);
        return ms.ToArray();
    }

    static byte[] DecodeStreaming(LZ4Decoder decoder, ReadOnlySpan<byte> compressed, int inputChunk, int outputChunk)
    {
        var ms = new MemoryStream();
        var output = new byte[outputChunk];
        var remaining = compressed;
        var status = OperationStatus.NeedMoreData;
        while (remaining.Length > 0)
        {
            var piece = remaining.Slice(0, Math.Min(inputChunk, remaining.Length));
            status = decoder.Decompress(piece, output, out var consumed, out var written);
            Assert.NotEqual(OperationStatus.InvalidData, status);
            Assert.True(consumed > 0 || written > 0, "no progress");
            ms.Write(output, 0, written);
            remaining = remaining.Slice(consumed);
            if (status == OperationStatus.Done) decoder.Reset();
        }
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = decoder.Decompress(ReadOnlySpan<byte>.Empty, output, out _, out var written);
            ms.Write(output, 0, written);
        }
        Assert.Equal(OperationStatus.Done, status);
        return ms.ToArray();
    }

    [Fact]
    public void RoundTrip_OneShot()
    {
        var compressed = LZ4.Compress(Data);
        Assert.Equal(Data, LZ4.Decompress(compressed));
        Assert.Equal(Data, LZ4.Decompress(compressed, trustedData: true));

        var dest = new byte[Data.Length];
        Assert.Equal(Data.Length, LZ4.Decompress(compressed, dest));
        Assert.Equal(Data, dest);
    }

    [Fact]
    public void Encoder_WritesFrameHeaderByDefault()
    {
        using var encoder = new LZ4Encoder();

        var compressed = EncodeStreaming(encoder, Utf8("header please"), 4096);

        // magic number 0x184D2204 little endian
        Assert.Equal(new byte[] { 0x04, 0x22, 0x4D, 0x18 }, compressed.AsSpan(0, 4).ToArray());
        Assert.True(LZ4.TryGetFrameInfo(compressed, out _));
        Assert.Equal(Utf8("header please"), LZ4.Decompress(compressed));
    }

    [Fact]
    public void Encoder_Reset_AbandonsFrameInProgress()
    {
        using var encoder = new LZ4Encoder();
        var buffer = new byte[encoder.GetMaxCompressedLength(1024)];

        // start a frame and buffer some data, then abandon it
        encoder.CompressDone(Utf8("abandoned data"), buffer);
        encoder.Reset();

        // the next frame starts with a fresh header and does not contain the abandoned bytes
        var fresh = EncodeStreaming(encoder, Utf8("fresh frame"), 4096);
        Assert.Equal(new byte[] { 0x04, 0x22, 0x4D, 0x18 }, fresh.AsSpan(0, 4).ToArray());
        Assert.Equal(Utf8("fresh frame"), LZ4.Decompress(fresh));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(70_000)]
    public void RoundTrip_Streaming(int chunk)
    {
        using var encoder = new LZ4Encoder(LZ4CompressionOptions.Default with { ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled });
        var compressed = EncodeStreaming(encoder, Data, chunk);
        Assert.Equal(Data, LZ4.Decompress(compressed));

        using var decoder = new LZ4Decoder();
        Assert.Equal(Data, DecodeStreaming(decoder, compressed, inputChunk: 777, outputChunk: 4096));
    }

    [Fact]
    public void Encoder_ReusableAfterClose()
    {
        using var encoder = new LZ4Encoder();
        var a = EncodeStreaming(encoder, Data, 8192);
        var b = EncodeStreaming(encoder, Utf8("second frame"), 8192);
        Assert.Equal(Data, LZ4.Decompress(a));
        Assert.Equal(Utf8("second frame"), LZ4.Decompress(b));
    }

    // Drives the encoder with destinations of pieceSize, the way a caller with a small buffer does.
    static byte[] EncodeInPieces(LZ4Encoder encoder, ReadOnlySpan<byte> data, int inputChunk, int pieceSize)
    {
        var ms = new MemoryStream();
        var piece = new byte[pieceSize];
        var remaining = data;
        while (true)
        {
            var isFinal = remaining.Length <= inputChunk;
            var input = isFinal ? remaining : remaining.Slice(0, inputChunk);
            OperationStatus status;
            do
            {
                status = encoder.Compress(input, piece, out var consumed, out var written, isFinal);
                Assert.NotEqual(OperationStatus.InvalidData, status);
                Assert.True(status == OperationStatus.Done || consumed > 0 || written > 0, "no progress");
                ms.Write(piece, 0, written);
                input = input.Slice(consumed);
            } while (status == OperationStatus.DestinationTooSmall || input.Length > 0);

            if (isFinal) break;
            remaining = remaining.Slice(inputChunk);
        }
        return ms.ToArray();
    }

    [Fact]
    public void Encoder_FinalBlock_EndsTheFrame()
    {
        using var encoder = new LZ4Encoder();
        var buffer = new byte[encoder.GetMaxCompressedLength(Data.Length)];
        var written = encoder.CompressDone(Data, buffer, isFinalBlock: true);
        Assert.Equal(Data, LZ4.Decompress(buffer.AsSpan(0, written)));

        // the next call starts another frame
        written = encoder.CompressDone(Utf8("next"), buffer, isFinalBlock: true);
        Assert.Equal(Utf8("next"), LZ4.Decompress(buffer.AsSpan(0, written)));
    }

    [Fact]
    public void Encoder_Flush_OutsideAFrame_WritesNothing()
    {
        using var encoder = new LZ4Encoder();
        var buffer = new byte[encoder.GetMaxCompressedLength(100)];
        Assert.Equal(0, encoder.FlushDone(buffer)); // fresh
        Assert.Equal(0, encoder.FlushDone(Span<byte>.Empty));

        encoder.CloseDone(buffer.AsSpan(encoder.CompressDone(Utf8("data"), buffer)));
        Assert.Equal(0, encoder.FlushDone(buffer)); // closed

        encoder.CompressDone(Utf8("abandoned"), buffer);
        encoder.Reset();
        Assert.Equal(0, encoder.FlushDone(buffer)); // reset

        Assert.Equal(Utf8("next"), LZ4.Decompress(EncodeStreaming(encoder, Utf8("next"), 4096)));
    }

    // Without autoFlush the blocks do not depend on how the input and the output are cut,
    // so a destination smaller than a block, or than the header, gives the same frame.
    [Theory]
    [InlineData(1, false)]
    [InlineData(13, false)]
    [InlineData(1000, false)]
    [InlineData(1, true)]
    [InlineData(70_000, true)]
    public void Encoder_Compress_DestinationSmallerThanABlock_MakesProgress(int pieceSize, bool blockChecksum)
    {
        var options = LZ4CompressionOptions.Default with
        {
            ContentSize = (ulong)Data.Length, // a 15 byte header
            ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled,
            BlockChecksumFlag = blockChecksum ? BlockChecksum.BlockChecksumEnabled : BlockChecksum.NoBlockChecksum,
        };
        using var encoder = new LZ4Encoder(options);
        var pieced = EncodeInPieces(encoder, Data, inputChunk: 50_000, pieceSize);

        using var whole = new LZ4Encoder(options);
        Assert.Equal(EncodeStreaming(whole, Data, 50_000), pieced);
        Assert.Equal(Data, LZ4.Decompress(pieced));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(9, false)]
    [InlineData(1000, true)]
    public void Encoder_Compress_AutoFlush_SmallDestination_RoundTrips(int pieceSize, bool independent)
    {
        using var encoder = new LZ4Encoder(LZ4CompressionOptions.Default with
        {
            AutoFlush = true,
            BlockMode = independent ? BlockMode.BlockIndependent : BlockMode.BlockLinked,
            BlockChecksumFlag = BlockChecksum.BlockChecksumEnabled,
        });
        Assert.Equal(Data, LZ4.Decompress(EncodeInPieces(encoder, Data, inputChunk: 30_000, pieceSize)));
        Assert.Equal(Utf8("next"), LZ4.Decompress(EncodeInPieces(encoder, Utf8("next"), inputChunk: 2, pieceSize)));
    }

    // Flush and Close keep what does not fit and continue on the next call, the output is the same as with room for all of it.
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    public void Encoder_FlushAndClose_SmallDestination_ContinueOnTheNextCall(int pieceSize)
    {
        var options = LZ4CompressionOptions.Default with { ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled };
        var first = Data.AsSpan(0, 10_000).ToArray();
        var second = Data.AsSpan(10_000, 10_000).ToArray();

        byte[] expected;
        using (var twin = new LZ4Encoder(options))
        {
            var big = new byte[twin.GetMaxCompressedLength(first.Length + second.Length)];
            var ms = new MemoryStream();
            ms.Write(big, 0, twin.CompressDone(first, big));
            ms.Write(big, 0, twin.FlushDone(big));
            ms.Write(big, 0, twin.CompressDone(second, big));
            ms.Write(big, 0, twin.CloseDone(big));
            expected = ms.ToArray();
        }

        using var encoder = new LZ4Encoder(options);
        var output = new MemoryStream();
        var buffer = new byte[encoder.GetMaxCompressedLength(first.Length)];
        var piece = new byte[pieceSize];

        output.Write(buffer, 0, encoder.CompressDone(first, buffer));
        var calls = 0;
        OperationStatus status;
        do
        {
            status = encoder.Flush(piece, out var written);
            output.Write(piece, 0, written);
            calls++;
        } while (status == OperationStatus.DestinationTooSmall);
        Assert.Equal(OperationStatus.Done, status);
        Assert.True(calls > 1);

        output.Write(buffer, 0, encoder.CompressDone(second, buffer));
        calls = 0;
        do
        {
            status = encoder.Close(piece, out var written);
            output.Write(piece, 0, written);
            calls++;
        } while (status == OperationStatus.DestinationTooSmall);
        Assert.Equal(OperationStatus.Done, status);
        Assert.True(calls > 1);

        Assert.Equal(expected, output.ToArray());
        Assert.Equal(Utf8("next"), LZ4.Decompress(EncodeStreaming(encoder, Utf8("next"), 4096)));
    }

    // A destination of exactly the right size is filled completely, which must not be taken for unfinished output.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Encoder_FlushAndClose_ExactDestination_FinishInOneCall(bool checksum, bool flushBeforeClose)
    {
        var options = LZ4CompressionOptions.Default with { ContentChecksumFlag = checksum ? ContentChecksum.ContentChecksumEnabled : ContentChecksum.NoContentChecksum };
        var first = Compressible(10_000, seed: 52);
        var second = Compressible(10_000, seed: 53);

        // the same input gives the same output, so a twin encoder tells the exact sizes
        int flushed, lastFlushed = 0, closed;
        using (var twin = new LZ4Encoder(options))
        {
            var scratch = new byte[twin.GetMaxCompressedLength(first.Length + second.Length)];
            twin.CompressDone(first, scratch);
            flushed = twin.FlushDone(scratch);
            twin.CompressDone(second, scratch);
            if (flushBeforeClose) lastFlushed = twin.FlushDone(scratch);
            closed = twin.CloseDone(scratch);
        }

        using var encoder = new LZ4Encoder(options);
        var output = new MemoryStream();
        var buffer = new byte[encoder.GetMaxCompressedLength(first.Length)];
        output.Write(buffer, 0, encoder.CompressDone(first, buffer));

        var flushBuffer = new byte[flushed];
        Assert.Equal(flushed, encoder.FlushDone(flushBuffer));
        output.Write(flushBuffer);

        output.Write(buffer, 0, encoder.CompressDone(second, buffer));
        if (flushBeforeClose)
        {
            var lastFlushBuffer = new byte[lastFlushed];
            Assert.Equal(lastFlushed, encoder.FlushDone(lastFlushBuffer));
            output.Write(lastFlushBuffer);
        }

        var closeBuffer = new byte[closed]; // only the footer when flushed before
        Assert.Equal(closed, encoder.CloseDone(closeBuffer));
        output.Write(closeBuffer);

        Assert.Equal(first.Concat(second).ToArray(), LZ4.Decompress(output.ToArray()));
        Assert.Equal(Utf8("next"), LZ4.Decompress(EncodeStreaming(encoder, Utf8("next"), 4096)));
    }

    // nothing is written when there is no room at all, so the frame goes on
    [Fact]
    public void Encoder_Flush_EmptyDestination_KeepsFrame()
    {
        using var encoder = new LZ4Encoder();
        var data = Data.AsSpan(0, 10_000).ToArray();
        var buffer = new byte[encoder.GetMaxCompressedLength(data.Length)];
        var written = encoder.CompressDone(data, buffer);

        Assert.Equal(OperationStatus.DestinationTooSmall, encoder.Flush(Span<byte>.Empty, out var none));
        Assert.Equal(0, none);
        written += encoder.FlushDone(buffer.AsSpan(written));
        written += encoder.CloseDone(buffer.AsSpan(written));
        Assert.Equal(data, LZ4.Decompress(buffer.AsSpan(0, written)));
    }

    [Fact]
    public void Encoder_UnfinishedClose_ContinuesWithFinalBlock()
    {
        using var encoder = new LZ4Encoder(LZ4CompressionOptions.Default with { ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled });
        var data = Data.AsSpan(0, 10_000).ToArray();
        var buffer = new byte[encoder.GetMaxCompressedLength(data.Length)];
        var small = new byte[2];
        var output = new MemoryStream();

        output.Write(buffer, 0, encoder.CompressDone(data, buffer));
        Assert.Equal(OperationStatus.DestinationTooSmall, encoder.Close(small, out var written));
        output.Write(small, 0, written);
        Assert.Equal(OperationStatus.DestinationTooSmall, encoder.Compress(ReadOnlySpan<byte>.Empty, small, out _, out written, isFinalBlock: true));
        output.Write(small, 0, written);
        output.Write(buffer, 0, encoder.CompressDone(ReadOnlySpan<byte>.Empty, buffer, isFinalBlock: true));

        Assert.Equal(data, LZ4.Decompress(output.ToArray()));
    }

    // once the frame ending has started, new input or a flush would break the frame
    [Fact]
    public void Encoder_UnfinishedClose_RejectsInputUntilReset()
    {
        using var encoder = new LZ4Encoder();
        var data = Data.AsSpan(0, 10_000).ToArray();
        var buffer = new byte[encoder.GetMaxCompressedLength(data.Length)];

        encoder.CompressDone(data, buffer);
        Assert.Equal(OperationStatus.DestinationTooSmall, encoder.Close(new byte[2], out _));
        Assert.Equal(OperationStatus.InvalidData, encoder.Compress(data, buffer, out var consumed, out _, isFinalBlock: false));
        Assert.Equal(0, consumed);
        Assert.Equal(OperationStatus.InvalidData, encoder.Flush(buffer, out _));
        Assert.Equal(OperationStatus.InvalidData, encoder.Close(buffer, out _));

        encoder.Reset();
        Assert.Equal(Utf8("next"), LZ4.Decompress(EncodeStreaming(encoder, Utf8("next"), 4096)));
    }

    [Fact]
    public void Encoder_OptionsApplyToItsFrames()
    {
        using var encoder = new LZ4Encoder();
        var plain = EncodeStreaming(encoder, Data, 8192);

        using var hcEncoder = new LZ4Encoder(LZ4CompressionOptions.Default with { CompressionLevel = 9, ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled });
        var hc = EncodeStreaming(hcEncoder, Data, 8192);

        Assert.True(hc.Length < plain.Length);
        Assert.Equal(Data, LZ4.Decompress(plain));
        Assert.Equal(Data, LZ4.Decompress(hc));
        Assert.True(LZ4.TryGetFrameInfo(hc, out var info));
        Assert.Equal(ContentChecksum.ContentChecksumEnabled, info.ContentChecksumFlag);
    }

    [Fact]
    public void Stream_RoundTrip()
    {
        var ms = new MemoryStream();
        using (var zs = new LZ4Stream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            zs.Write(Data);
        }
        var compressed = ms.ToArray();
        Assert.True(LZ4.TryGetFrameInfo(compressed, out _));
        Assert.Equal(Data, LZ4.Decompress(compressed));

        using var reader = new LZ4Stream(new MemoryStream(compressed), CompressionMode.Decompress);
        var result = new MemoryStream();
        reader.CopyTo(result);
        Assert.Equal(Data, result.ToArray());
    }

    [Fact]
    public void Stream_CorruptInput_Throws()
    {
        var garbage = new byte[4096];
        new Random(1).NextBytes(garbage);
        using var reader = new LZ4Stream(new MemoryStream(garbage), CompressionMode.Decompress);
        Assert.Throws<LZ4Exception>(() => reader.CopyTo(Stream.Null));
    }

    [Fact]
    public void DoubleDispose_And_UseAfterDispose()
    {
        var encoder = new LZ4Encoder();
        var decoder = new LZ4Decoder();
        encoder.Dispose();
        encoder.Dispose();
        decoder.Dispose();
        decoder.Dispose();
        Assert.True(encoder.IsDisposed);
        Assert.True(decoder.IsDisposed);

        var buffer = new byte[128];
        Assert.Throws<ObjectDisposedException>(() => encoder.Compress(Utf8("x"), buffer, out _, out _, isFinalBlock: false));
        Assert.Throws<ObjectDisposedException>(() => encoder.Flush(buffer, out _));
        Assert.Throws<ObjectDisposedException>(() => encoder.Close(buffer, out _));
        Assert.Throws<ObjectDisposedException>(() => encoder.Reset());
        Assert.Throws<ObjectDisposedException>(() => encoder.GetMaxCompressedLength(1));
        Assert.Throws<ObjectDisposedException>(() => encoder.Reset());
        Assert.Throws<ObjectDisposedException>(() => decoder.Decompress(buffer, buffer, out _, out _));
        Assert.Throws<ObjectDisposedException>(() => decoder.Reset());
        Assert.Throws<ObjectDisposedException>(() => decoder.GetFrameInfo(buffer, out _));
    }

    [Fact]
    public void Finalizer_ReleasesUndisposedContexts()
    {
        CreateAndDrop();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        static void CreateAndDrop()
        {
            var encoder = new LZ4Encoder();
            var decoder = new LZ4Decoder();
            var dict = LZ4Dictionary.Create(Utf8("dictionary"));
            var buffer = new byte[256];
            encoder.CompressDone(Utf8("abc"), buffer);
            decoder.Decompress(buffer, new byte[16], out _, out _);
        }
    }

    [Fact]
    public void Decoder_InvalidData_ReportsZeroHint_AndResetRecovers()
    {
        using var decoder = new LZ4Decoder();
        var dest = new byte[256];
        var status = decoder.Decompress(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, dest, out _, out _, out var hint);
        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(0, hint);

        decoder.Reset();
        var compressed = LZ4.Compress(Utf8("ok"));
        Assert.Equal(OperationStatus.Done, decoder.Decompress(compressed, dest, out _, out var written));
        Assert.Equal(Utf8("ok"), dest.AsSpan(0, written).ToArray());
    }

    [Fact]
    public void Dictionary_Create_RoundTrip()
    {
        var dictBytes = Utf8(string.Concat(Enumerable.Repeat("lz4 native compression dotnet dictionary ", 64)));
        using var dict = LZ4Dictionary.Create(dictBytes, dictionaryId: 42);

        Assert.Equal(dictBytes, dict.Data.ToArray());
        Assert.Equal(42u, dict.DictionaryId);
        Assert.False(dict.IsDisposed);

        var options = LZ4CompressionOptions.Default with { Dictionary = dict };
        Assert.Equal(42u, options.DictionaryId);
        var compressed = LZ4.Compress(Data, options);

        Assert.True(LZ4.TryGetFrameInfo(compressed, out var info));
        Assert.Equal(42u, info.DictionaryId);

        var decompressionOptions = LZ4DecompressionOptions.Default with { Dictionary = dict };
        Assert.Equal(Data, LZ4.Decompress(compressed, decompressionOptions));

        // streaming with the dictionary
        using var encoder = new LZ4Encoder(options);
        var streamed = EncodeStreaming(encoder, Data, 8192);
        using var decoder = new LZ4Decoder(decompressionOptions);
        Assert.Equal(Data, DecodeStreaming(decoder, streamed, 1000, 4096));

        Assert.Throws<ArgumentException>(() => LZ4Dictionary.Create(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Dictionary_Dispose_AndUseAfterDispose()
    {
        var dict = LZ4Dictionary.Create(Utf8("dictionary"));
        dict.Dispose();
        dict.Dispose();
        Assert.True(dict.IsDisposed);

        Assert.Throws<ObjectDisposedException>(() => LZ4.Compress(Data, LZ4CompressionOptions.Default with { Dictionary = dict }));
        Assert.Throws<ObjectDisposedException>(() => new LZ4Encoder(LZ4CompressionOptions.Default with { Dictionary = dict }));
        Assert.Throws<ObjectDisposedException>(() => new LZ4Decoder(LZ4DecompressionOptions.Default with { Dictionary = dict }));
        Assert.Throws<ObjectDisposedException>(() => LZ4.Decompress(LZ4.Compress(Data), LZ4DecompressionOptions.Default with { Dictionary = dict }));
    }

    [Fact]
    public void Dictionary_StaysAliveWhileEncoderUsesIt()
    {
        var encoder = CreateEncoderAndDropDictionary();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var compressed = EncodeStreaming(encoder, Data, 8192);
        using var dict = LZ4Dictionary.Create(Utf8(string.Concat(Enumerable.Repeat("lz4 native compression dotnet dictionary ", 64))));
        Assert.Equal(Data, LZ4.Decompress(compressed, LZ4DecompressionOptions.Default with { Dictionary = dict }));
        encoder.Dispose();

        static LZ4Encoder CreateEncoderAndDropDictionary()
        {
            var dict = LZ4Dictionary.Create(Utf8(string.Concat(Enumerable.Repeat("lz4 native compression dotnet dictionary ", 64))));
            return new LZ4Encoder(LZ4CompressionOptions.Default with { Dictionary = dict });
        }
    }

    [Fact]
    public void Dictionary_DisposedWhileEncoderUsesIt()
    {
        var dictBytes = Utf8(string.Concat(Enumerable.Repeat("lz4 native compression dotnet dictionary ", 64)));
        var dict = LZ4Dictionary.Create(dictBytes);
        var options = LZ4CompressionOptions.Default with { Dictionary = dict, BlockMode = BlockMode.BlockIndependent };

        // start a frame, then the owner lets go of the dictionary while the encoder still needs it for every block
        using var encoder = new LZ4Encoder(options);
        var buffer = new byte[encoder.GetMaxCompressedLength(8192)];
        var ms = new MemoryStream();
        var written = encoder.CompressDone(Data.AsSpan(0, 8192), buffer);
        ms.Write(buffer, 0, written);

        dict.Dispose();
        Assert.True(dict.IsDisposed);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var remaining = Data.AsSpan(8192);
        while (remaining.Length > 0)
        {
            var piece = remaining.Slice(0, Math.Min(8192, remaining.Length));
            written = encoder.CompressDone(piece, buffer);
            ms.Write(buffer, 0, written);
            remaining = remaining.Slice(piece.Length);
        }
        written = encoder.CloseDone(buffer);
        ms.Write(buffer, 0, written);

        // the frames that follow keep using it as well
        var second = EncodeStreaming(encoder, Data, 4096);

        using var same = LZ4Dictionary.Create(dictBytes);
        var decompressionOptions = LZ4DecompressionOptions.Default with { Dictionary = same };
        Assert.Equal(Data, LZ4.Decompress(ms.ToArray(), decompressionOptions));
        Assert.Equal(Data, LZ4.Decompress(second, decompressionOptions));

        // a disposed dictionary is still refused for new use
        Assert.Throws<ObjectDisposedException>(() => new LZ4Encoder(options));
    }

    [Fact]
    public void Dictionary_SharedByManyEncoders()
    {
        var dictBytes = Utf8(string.Concat(Enumerable.Repeat("lz4 native compression dotnet dictionary ", 64)));
        var dict = LZ4Dictionary.Create(dictBytes);
        var options = LZ4CompressionOptions.Default with { Dictionary = dict };

        var encoders = Enumerable.Range(0, 8).Select(_ => new LZ4Encoder(options)).ToArray();
        dict.Dispose();
        dict.Dispose(); // twice is fine

        using var same = LZ4Dictionary.Create(dictBytes);
        foreach (var encoder in encoders)
        {
            Assert.Equal(Data, LZ4.Decompress(EncodeStreaming(encoder, Data, 8192), LZ4DecompressionOptions.Default with { Dictionary = same }));
            encoder.Dispose();
        }
    }

    [Fact]
    public void Dictionary_ReleasedByDispose()
    {
        var dictBytes = Utf8(string.Concat(Enumerable.Repeat("lz4 native compression dotnet dictionary ", 64)));
        var first = LZ4Dictionary.Create(dictBytes);
        var second = LZ4Dictionary.Create(dictBytes);

        var other = new LZ4Encoder(LZ4CompressionOptions.Default with { Dictionary = first });
        var encoder = new LZ4Encoder(LZ4CompressionOptions.Default with { Dictionary = second });
        other.Dispose();
        first.Dispose(); // no longer referenced by any encoder, so freed right here
        Assert.Equal(Data, LZ4.Decompress(EncodeStreaming(encoder, Data, 8192), LZ4DecompressionOptions.Default with { Dictionary = second }));

        encoder.Reset(); // keeps the dictionary
        Assert.Equal(Data, LZ4.Decompress(EncodeStreaming(encoder, Data, 8192), LZ4DecompressionOptions.Default with { Dictionary = second }));

        second.Dispose();
        encoder.Dispose(); // the last reference, the native dictionary goes with it
        encoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => encoder.Compress(Data, new byte[Data.Length * 2], out _, out _, isFinalBlock: false));
    }

    [Fact]
    public void Dictionary_DisposedWhileDecoderUsesIt()
    {
        var dictBytes = Utf8(string.Concat(Enumerable.Repeat("lz4 native compression dotnet dictionary ", 64)));
        byte[] compressed;
        using (var compressDict = LZ4Dictionary.Create(dictBytes))
        {
            // independent blocks, so every block of the frame reads the dictionary bytes
            compressed = LZ4.Compress(Data, LZ4CompressionOptions.Default with { Dictionary = compressDict, BlockMode = BlockMode.BlockIndependent });
        }

        var dict = LZ4Dictionary.Create(dictBytes);
        var options = LZ4DecompressionOptions.Default with { Dictionary = dict };
        using var decoder = new LZ4Decoder(options);

        // start the frame, then the owner lets go of the dictionary
        var output = new byte[Data.Length];
        var status = decoder.Decompress(compressed.AsSpan(0, 1000), output, out var consumed, out var written);
        Assert.NotEqual(OperationStatus.InvalidData, status);

        dict.Dispose();
        Assert.True(dict.IsDisposed);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var ms = new MemoryStream();
        ms.Write(output, 0, written);
        ms.Write(DecodeStreaming(decoder, compressed.AsSpan(consumed), 1000, 4096));
        Assert.Equal(Data, ms.ToArray());

        // the frames that follow keep using it as well
        decoder.Reset();
        Assert.Equal(Data, DecodeStreaming(decoder, compressed, 1000, 4096));

        // a disposed dictionary is still refused for new use
        Assert.Throws<ObjectDisposedException>(() => new LZ4Decoder(options));
    }

    [Fact]
    public unsafe void Dictionary_SurvivesGCMoveDuringIndependentBlockFrame()
    {
        // lz4frame keeps the dictionary address it saw at the first call for the whole frame.
        // With independent blocks every block reads from that address, so the bytes must stay put
        // between Decompress calls even when the GC compacts the heap.
        var dictBytes = Utf8(string.Concat(Enumerable.Repeat("lz4 native compression dotnet dictionary ", 64)));
        byte[] compressed;
        using (var compressDict = LZ4Dictionary.Create(dictBytes))
        {
            compressed = LZ4.Compress(Data, LZ4CompressionOptions.Default with { Dictionary = compressDict, BlockMode = BlockMode.BlockIndependent });
        }

        var output = new byte[Data.Length];
        LZ4Dictionary dict;
        LZ4Decoder decoder;
        int consumed, written;
        var attempt = 0;
        while (true)
        {
            attempt++;
            // surrounding allocations shape where the dictionary lands, so vary them until a collection moves it
            var before = new byte[attempt * 8 * 1024];
            dict = LZ4Dictionary.Create(dictBytes);
            var after = new byte[attempt * 8 * 1024];
            GC.KeepAlive(before);
            GC.KeepAlive(after);

            decoder = new LZ4Decoder(LZ4DecompressionOptions.Default with { Dictionary = dict });
            var status = decoder.Decompress(compressed.AsSpan(0, 1000), output, out consumed, out written, out _);
            Assert.NotEqual(OperationStatus.InvalidData, status);

            byte* addressSeenByNative;
            fixed (byte* p = dict.Data.Span) addressSeenByNative = p;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            byte* addressAfterGC;
            fixed (byte* p = dict.Data.Span) addressAfterGC = p;

            if (addressSeenByNative != addressAfterGC || attempt == 40) break; // a stable address is also fine, then this is a plain round trip

            decoder.Dispose();
            dict.Dispose();
        }

        using (decoder)
        using (dict)
        {
            for (var i = 0; i < 256; i++)
            {
                new byte[32 * 1024].AsSpan().Fill(0xFF); // reuse and overwrite the memory the dictionary used to live in
            }

            var total = written;
            var remaining = compressed.AsSpan(consumed);
            var status = OperationStatus.NeedMoreData;
            while (remaining.Length > 0)
            {
                var piece = remaining.Slice(0, Math.Min(1000, remaining.Length));
                status = decoder.Decompress(piece, output.AsSpan(total), out consumed, out written, out _);
                Assert.NotEqual(OperationStatus.InvalidData, status);
                total += written;
                remaining = remaining.Slice(consumed);
            }

            Assert.Equal(OperationStatus.Done, status);
            Assert.Equal(Data, output.AsSpan(0, total).ToArray());
        }
    }

    [Fact]
    public void Stream_ConstructorRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new LZ4Stream(new MemoryStream(), (LZ4Encoder)null!));
        Assert.Throws<ArgumentNullException>(() => new LZ4Stream(new MemoryStream(), (LZ4Decoder)null!));
    }
}
