using System.Buffers;
using System.Text;
using BclCompressionOptions = System.IO.Compression.ZstandardCompressionOptions;
using BclDecoder = System.IO.Compression.ZstandardDecoder;
using BclEncoder = System.IO.Compression.ZstandardEncoder;
using BclStream = System.IO.Compression.ZstandardStream;
using CompressionMode = System.IO.Compression.CompressionMode;

namespace NativeCompressions.Tests;

// Covers SetSourceLength, SetPrefix, TryGetMaxDecompressedLength and the window log constants.
public class ZstandardAdditionsTest
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] SampleData() => Utf8(string.Concat(Enumerable.Repeat("zstd native compression dotnet additions sample ", 2000)));

    static byte[] BclDecompress(byte[] compressed)
    {
        using var zs = new BclStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var ms = new MemoryStream();
        zs.CopyTo(ms);
        return ms.ToArray();
    }

    // feed in two pieces so the encoder cannot infer the size from a single shot
    static byte[] CompressInTwoPieces(ZstandardEncoder encoder, byte[] data, bool close = true)
    {
        var ms = new MemoryStream();
        var output = new byte[Zstandard.GetMaxCompressedLength(data.Length) + 64];
        var half = data.Length / 2;

        var status = encoder.Compress(data.AsSpan(0, half), output, out var consumed, out var written, isFinalBlock: false);
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(half, consumed);
        ms.Write(output, 0, written);

        status = encoder.Compress(data.AsSpan(half), output, out consumed, out written, isFinalBlock: close);
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(data.Length - half, consumed);
        ms.Write(output, 0, written);
        return ms.ToArray();
    }

    // ---- SetSourceLength

    [Fact]
    public void SetSourceLength_RecordsContentSizeInFrameHeader()
    {
        var data = SampleData();
        using var encoder = new ZstandardEncoder();

        // without pledged size a streamed frame has no content size
        var unknown = CompressInTwoPieces(encoder, data);
        Assert.False(Zstandard.TryGetFrameContentSize(unknown, out _));

        encoder.Reset();
        encoder.SetSourceLength(data.Length);
        var pledged = CompressInTwoPieces(encoder, data);

        Assert.True(Zstandard.TryGetFrameContentSize(pledged, out var size));
        Assert.Equal((ulong)data.Length, size);
        Assert.Equal(data, BclDecompress(pledged));
        Assert.Equal(data, Zstandard.Decompress(pledged, trustedData: true));

        // pledged size applies to the next frame only
        var next = CompressInTwoPieces(encoder, data);
        Assert.False(Zstandard.TryGetFrameContentSize(next, out _));
    }

    [Fact]
    public void SetSourceLength_MismatchFailsAtClose()
    {
        var data = SampleData();
        using var encoder = new ZstandardEncoder();
        encoder.SetSourceLength(data.Length + 1);

        var output = new byte[Zstandard.GetMaxCompressedLength(data.Length) + 64];
        Assert.Equal(OperationStatus.Done, encoder.Compress(data.AsSpan(0, 10), output, out _, out _, isFinalBlock: false));
        Assert.Equal(OperationStatus.InvalidData, encoder.Compress(data.AsSpan(10), output, out _, out _, isFinalBlock: true));
    }

    [Fact]
    public void SetSourceLength_Validation()
    {
        using var encoder = new ZstandardEncoder();
        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.SetSourceLength(-1));

        // not allowed once the frame has started
        var output = new byte[4096];
        encoder.Compress(Utf8("abc"), output, out _, out _, isFinalBlock: false);
        Assert.Throws<ZstandardException>(() => encoder.SetSourceLength(100));
    }

    [Fact]
    public void Stream_SetSourceLength_RecordsContentSize()
    {
        var data = SampleData();
        var ms = new MemoryStream();
        using (var zs = new ZstandardStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            zs.SetSourceLength(data.Length);
            zs.Write(data, 0, data.Length / 3);
            zs.Write(data, data.Length / 3, data.Length - data.Length / 3);
        }

        var compressed = ms.ToArray();
        Assert.True(Zstandard.TryGetFrameContentSize(compressed, out var size));
        Assert.Equal((ulong)data.Length, size);
        Assert.Equal(data, BclDecompress(compressed));

        using var reader = new ZstandardStream(new MemoryStream(compressed), CompressionMode.Decompress);
        Assert.Throws<InvalidOperationException>(() => reader.SetSourceLength(1));
    }

    // ---- SetPrefix

    static byte[] Prefix() => Utf8(string.Concat(Enumerable.Repeat("zstd native compression dotnet additions ", 64)));

    [Fact]
    public void SetPrefix_NativeEncoder_BclDecoder()
    {
        var data = SampleData();
        var prefix = Prefix();

        // a checksum makes decoding with the wrong prefix fail instead of producing garbage
        using var encoder = new ZstandardEncoder(ZstandardCompressionOptions.Default with { CompressionLevel = 3, ChecksumFlag = true });
        encoder.SetPrefix(prefix);
        var withPrefix = CompressInTwoPieces(encoder, data);

        // BCL needs the same prefix
        using (var decoder = new BclDecoder())
        {
            decoder.SetPrefix(prefix);
            var dest = new byte[data.Length];
            Assert.Equal(OperationStatus.Done, decoder.Decompress(withPrefix, dest, out _, out var written));
            Assert.Equal(data, dest.AsSpan(0, written).ToArray());
        }

        // and fails without it
        using (var decoder = new BclDecoder())
        {
            var dest = new byte[data.Length];
            Assert.NotEqual(OperationStatus.Done, decoder.Decompress(withPrefix, dest, out _, out _));
        }
    }

    [Fact]
    public void SetPrefix_BclEncoder_NativeDecoder()
    {
        var data = SampleData();
        var prefix = Prefix();

        var ms = new MemoryStream();
        using (var encoder = new BclEncoder(3))
        {
            encoder.SetPrefix(prefix);
            var output = new byte[BclEncoder.GetMaxCompressedLength(data.Length)];
            Assert.Equal(OperationStatus.Done, encoder.Compress(data, output, out _, out var written, isFinalBlock: true));
            ms.Write(output, 0, written);
        }
        var compressed = ms.ToArray();

        using var decoder = new ZstandardDecoder();
        decoder.SetPrefix(prefix);
        var dest = new byte[data.Length];
        Assert.Equal(OperationStatus.Done, decoder.Decompress(compressed, dest, out _, out var w));
        Assert.Equal(data, dest.AsSpan(0, w).ToArray());

        // prefix is single use, the next frame without prefix must fail
        decoder.Reset();
        Assert.NotEqual(OperationStatus.Done, decoder.Decompress(compressed, dest, out _, out _));
    }

    [Fact]
    public void SetPrefix_RoundTripNativeOnly_AndReplace()
    {
        var data = SampleData();
        var prefix1 = Prefix();
        var prefix2 = Utf8(string.Concat(Enumerable.Repeat("completely different prefix content ", 64)));

        using var encoder = new ZstandardEncoder(ZstandardCompressionOptions.Default with { ChecksumFlag = true });
        using var decoder = new ZstandardDecoder();

        encoder.SetPrefix(prefix1);
        encoder.SetPrefix(prefix2); // replaces the first one
        var compressed = CompressInTwoPieces(encoder, data);

        var dest = new byte[data.Length];
        decoder.SetPrefix(prefix2);
        Assert.Equal(OperationStatus.Done, decoder.Decompress(compressed, dest, out _, out var written));
        Assert.Equal(data, dest.AsSpan(0, written).ToArray());

        decoder.Reset();
        decoder.SetPrefix(prefix1);
        Assert.NotEqual(OperationStatus.Done, decoder.Decompress(compressed, dest, out _, out _));
    }

    [Fact]
    public void SetPrefix_NotAllowedMidFrame()
    {
        using var encoder = new ZstandardEncoder();
        var output = new byte[4096];
        encoder.Compress(Utf8("abc"), output, out _, out _, isFinalBlock: false);
        Assert.Throws<ZstandardException>(() => encoder.SetPrefix(Prefix()));

        using var decoder = new ZstandardDecoder();
        var frame = Zstandard.Compress(SampleData());
        decoder.Decompress(frame.AsSpan(0, 20), output, out _, out _);
        Assert.Throws<ZstandardException>(() => decoder.SetPrefix(Prefix()));
    }

    [Fact]
    public void SetPrefix_AfterDisposeThrows()
    {
        var encoder = new ZstandardEncoder();
        encoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => encoder.SetPrefix(Prefix()));
        Assert.Throws<ObjectDisposedException>(() => encoder.SetSourceLength(1));

        var decoder = new ZstandardDecoder();
        decoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => decoder.SetPrefix(Prefix()));
    }

    // ---- TryGetMaxDecompressedLength

    [Fact]
    public void TryGetMaxDecompressedLength_MatchesBclAndCoversMultipleFrames()
    {
        var a = SampleData();
        var b = Utf8("second frame second frame second frame");

        using var encoder = new ZstandardEncoder();
        var frames = CompressInTwoPieces(encoder, a).Concat(Zstandard.Compress(b)).ToArray();

        Assert.True(Zstandard.TryGetMaxDecompressedLength(frames, out var length));
        Assert.True(length >= a.Length + b.Length);

        Assert.True(BclDecoder.TryGetMaxDecompressedLength(frames, out var bclLength));
        Assert.Equal(bclLength, length);

        // a frame with content size gives the exact value
        var exact = Zstandard.Compress(a);
        Assert.True(Zstandard.TryGetMaxDecompressedLength(exact, out var exactLength));
        Assert.Equal(a.Length, exactLength);
    }

    [Fact]
    public void Decompress_Trusted_DecodesAllFrames_WithUnknownSizeAndSkippable()
    {
        var a = SampleData();
        var b = Utf8("second frame second frame second frame");
        var c = Utf8("third frame");

        // frame without content size, a skippable frame, then two frames with content size
        using var encoder = new ZstandardEncoder();
        var unknown = CompressInTwoPieces(encoder, a);
        Assert.False(Zstandard.TryGetFrameContentSize(unknown, out _));

        var skippable = new byte[] { 0x50, 0x2A, 0x4D, 0x18, 3, 0, 0, 0, 0xAA, 0xBB, 0xCC };
        var frames = unknown.Concat(skippable).Concat(Zstandard.Compress(b)).Concat(Zstandard.Compress(c)).ToArray();
        var expected = a.Concat(b).Concat(c).ToArray();

        // the bound overestimates here, so the trusted path has to trim
        Assert.True(Zstandard.TryGetMaxDecompressedLength(frames, out var bound));
        Assert.True(bound > expected.Length);

        Assert.Equal(expected, Zstandard.Decompress(frames, trustedData: true));
        Assert.Equal(expected, Zstandard.Decompress(frames, trustedData: false));

        // all frames with content size give an exact bound and no trim
        var exact = Zstandard.Compress(a).Concat(skippable).Concat(Zstandard.Compress(b)).ToArray();
        Assert.True(Zstandard.TryGetMaxDecompressedLength(exact, out var exactBound));
        Assert.Equal(a.Length + b.Length, exactBound);
        Assert.Equal(a.Concat(b).ToArray(), Zstandard.Decompress(exact, trustedData: true));

        // trusted rejects the same bad input as the streaming path
        Assert.Throws<ZstandardException>(() => Zstandard.Decompress(frames.AsSpan(0, frames.Length / 2), trustedData: true));
        Assert.Throws<ZstandardException>(() => Zstandard.Decompress(frames.Concat(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).ToArray(), trustedData: true));
    }

    [Fact]
    public void TryGetMaxDecompressedLength_FalseOnBadInput()
    {
        var compressed = Zstandard.Compress(SampleData());

        Assert.False(Zstandard.TryGetMaxDecompressedLength(compressed.AsSpan(0, compressed.Length / 2), out var length));
        Assert.Equal(0, length);

        Assert.False(Zstandard.TryGetMaxDecompressedLength(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, out _));

        // no frames at all is a valid series with bound 0
        Assert.True(Zstandard.TryGetMaxDecompressedLength(ReadOnlySpan<byte>.Empty, out var empty));
        Assert.Equal(0, empty);
    }

    // ---- window log constants

    [Fact]
    public void WindowLogConstants_MatchBcl()
    {
        Assert.Equal(BclCompressionOptions.MinWindowLog2, Zstandard.MinWindowLog);
        Assert.Equal(BclCompressionOptions.MaxWindowLog2, Zstandard.MaxWindowLog);
        Assert.Equal(10, Zstandard.MinWindowLog);
        Assert.Equal(IntPtr.Size == 8 ? 31 : 30, Zstandard.MaxWindowLog);
        Assert.Equal(27, Zstandard.DefaultWindowLogMax);
    }

    [Fact]
    public void WindowLogConstants_BoundOptions()
    {
        Assert.Throws<ZstandardException>(() => new ZstandardEncoder(ZstandardCompressionOptions.Default with { WindowLog = Zstandard.MinWindowLog - 1 }));
        Assert.Throws<ZstandardException>(() => new ZstandardEncoder(ZstandardCompressionOptions.Default with { WindowLog = Zstandard.MaxWindowLog + 1 }));

        using var min = new ZstandardEncoder(ZstandardCompressionOptions.Default with { WindowLog = Zstandard.MinWindowLog });
        using var max = new ZstandardEncoder(ZstandardCompressionOptions.Default with { WindowLog = Zstandard.MaxWindowLog });

        // A streamed frame with unknown size records the full window in its header
        // (one-shot compression shrinks the window to the content size), so stream it and
        // check that a decoder rejects it when WindowLogMax is below that window.
        var data = SampleData();
        const int windowLog = 20;
        using var streamed = new ZstandardEncoder(ZstandardCompressionOptions.Default with { WindowLog = windowLog });
        var big = CompressInTwoPieces(streamed, data);
        Assert.False(Zstandard.TryGetFrameContentSize(big, out _));

        using (var decoder = new ZstandardDecoder(ZstandardDecompressionOptions.Default with { WindowLogMax = windowLog - 1 }))
        {
            Assert.Equal(OperationStatus.InvalidData, decoder.Decompress(big, new byte[data.Length], out _, out _));
        }
        using (var decoder = new ZstandardDecoder(ZstandardDecompressionOptions.Default with { WindowLogMax = windowLog }))
        {
            var dest = new byte[data.Length];
            Assert.Equal(OperationStatus.Done, decoder.Decompress(big, dest, out _, out var written));
            Assert.Equal(data, dest.AsSpan(0, written).ToArray());
        }
    }
}
