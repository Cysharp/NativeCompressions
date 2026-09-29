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
            var written = encoder.Compress(piece, buffer);
            ms.Write(buffer, 0, written);
            remaining = remaining.Slice(piece.Length);
        }
        var closed = encoder.Close(buffer);
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
        encoder.Compress(Utf8("abandoned data"), buffer);
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

    [Fact]
    public void Encoder_ResetWithOptions_AppliesToNextFrame()
    {
        using var encoder = new LZ4Encoder();
        var plain = EncodeStreaming(encoder, Data, 8192);

        encoder.Reset(LZ4CompressionOptions.Default with { CompressionLevel = 9, ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled });
        var hc = EncodeStreaming(encoder, Data, 8192);

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
        Assert.Throws<ObjectDisposedException>(() => encoder.Compress(Utf8("x"), buffer));
        Assert.Throws<ObjectDisposedException>(() => encoder.Flush(buffer));
        Assert.Throws<ObjectDisposedException>(() => encoder.Close(buffer));
        Assert.Throws<ObjectDisposedException>(() => encoder.GetMaxCompressedLength(1));
        Assert.Throws<ObjectDisposedException>(() => encoder.Reset(LZ4CompressionOptions.Default));
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
            encoder.Compress(Utf8("abc"), buffer);
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
        Assert.Equal(42u, options.DictionaryID);
        var compressed = LZ4.Compress(Data, options);

        Assert.True(LZ4.TryGetFrameInfo(compressed, out var info));
        Assert.Equal(42u, info.DictionaryID);

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
        using var decoder = new LZ4Decoder(LZ4DecompressionOptions.Default with { Dictionary = dict });
        Assert.Throws<ObjectDisposedException>(() => decoder.Decompress(LZ4.Compress(Data), new byte[Data.Length], out _, out _));
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
