using System.Text;
using BclDecoder = System.IO.Compression.ZstandardDecoder;
using BclEncoder = System.IO.Compression.ZstandardEncoder;
using BclStream = System.IO.Compression.ZstandardStream;
using CompressionMode = System.IO.Compression.CompressionMode;

namespace NativeCompressions.Tests;

// Static members of the Zstandard class: constants, bounds, span based one-shot APIs and frame inspection.
public class ZstandardStaticApiTest
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] SampleData() => Utf8(string.Concat(Enumerable.Repeat("zstd native compression dotnet static api sample ", 2000)));

    static byte[] BclDecompress(byte[] compressed)
    {
        using var zs = new BclStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var ms = new MemoryStream();
        zs.CopyTo(ms);
        return ms.ToArray();
    }

    // ---- constants

    [Fact]
    public void VersionMatchesNativeLibrary()
    {
        string expected;
        unsafe
        {
            expected = new string((sbyte*)ZstandardNativeMethods.ZSTD_versionString());
        }

        Assert.Equal(expected, Zstandard.Version);

        var parts = Zstandard.Version.Split('.').Select(int.Parse).ToArray();
        Assert.Equal(3, parts.Length);
        Assert.Equal((uint)(parts[0] * 10000 + parts[1] * 100 + parts[2]), Zstandard.VersionNumber);
    }

    [Fact]
    public void CompressionLevelBounds()
    {
        Assert.Equal(3, Zstandard.DefaultCompressionLevel);
        Assert.True(Zstandard.MinCompressionLevel < 0);
        Assert.True(Zstandard.MaxCompressionLevel >= 19);
        Assert.Equal(ZstandardNativeMethods.ZSTD_minCLevel(), Zstandard.MinCompressionLevel);
        Assert.Equal(ZstandardNativeMethods.ZSTD_maxCLevel(), Zstandard.MaxCompressionLevel);
        Assert.Equal(ZstandardNativeMethods.ZSTD_defaultCLevel(), Zstandard.DefaultCompressionLevel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(128 * 1024)]
    [InlineData(10 * 1024 * 1024)]
    public void GetMaxCompressedLength_MatchesNativeBoundAndBcl(int size)
    {
        var expected = (int)ZstandardNativeMethods.ZSTD_compressBound((nuint)size);
        Assert.Equal(expected, Zstandard.GetMaxCompressedLength(size));
        Assert.Equal((nuint)expected, Zstandard.GetMaxCompressedLength((nuint)size));
        Assert.Equal(BclEncoder.GetMaxCompressedLength(size), Zstandard.GetMaxCompressedLength(size));
        Assert.True(Zstandard.GetMaxCompressedLength(size) >= size);
    }

    [Fact]
    public void MaxFrameHeaderSize()
    {
        // ZSTD_FRAMEHEADERSIZE_MAX. Feeding this many bytes is always enough for TryGetFrameContentSize.
        Assert.Equal(18, Zstandard.MaxFrameHeaderSize);

        var data = SampleData();
        var compressed = Zstandard.Compress(data);
        Assert.True(Zstandard.TryGetFrameContentSize(compressed.AsSpan(0, Zstandard.MaxFrameHeaderSize), out var size));
        Assert.Equal((ulong)data.Length, size);
    }

    // ---- min / max level compress

    [Theory]
    [InlineData(-5)]
    [InlineData(1)]
    [InlineData(19)]
    [InlineData(22)]
    public void Compress_AnyLevel_BclDecodes(int level)
    {
        var data = SampleData();
        var compressed = Zstandard.Compress(data, level);
        Assert.Equal(data, BclDecompress(compressed));
        Assert.Equal(data, Zstandard.Decompress(compressed));
    }

    [Fact]
    public void Compress_MinAndMaxLevelConstants()
    {
        var data = SampleData();
        Assert.Equal(data, BclDecompress(Zstandard.Compress(data, Zstandard.MinCompressionLevel)));
        Assert.Equal(data, BclDecompress(Zstandard.Compress(data, Zstandard.MaxCompressionLevel)));
    }

    // ---- span based one-shot

    [Fact]
    public void Compress_ToSpan_WithLevel()
    {
        var data = SampleData();
        var dest = new byte[Zstandard.GetMaxCompressedLength(data.Length)];

        var written = Zstandard.Compress(data, dest, 5);
        Assert.True(written > 0 && written < data.Length);
        Assert.Equal(data, BclDecompress(dest.AsSpan(0, written).ToArray()));

        Assert.Throws<ZstandardException>(() => Zstandard.Compress(data, new byte[8], 5));
    }

    [Fact]
    public void Compress_ToSpan_WithOptions()
    {
        var data = SampleData();
        var dest = new byte[Zstandard.GetMaxCompressedLength(data.Length)];
        var options = ZstandardCompressionOptions.Default with { CompressionLevel = 9, ChecksumFlag = true };

        var written = Zstandard.Compress(data, dest, options);
        Assert.Equal(data, BclDecompress(dest.AsSpan(0, written).ToArray()));

        Assert.Throws<ZstandardException>(() => Zstandard.Compress(data, new byte[8], options));
    }

    [Fact]
    public void Compress_WithEncoder_ArrayAndSpan()
    {
        var data = SampleData();
        using var encoder = new ZstandardEncoder(ZstandardCompressionOptions.Default with { ChecksumFlag = true });

        var array = Zstandard.Compress(data, encoder);
        Assert.Equal(data, BclDecompress(array));

        // the encoder is reusable for the next frame
        var dest = new byte[Zstandard.GetMaxCompressedLength(data.Length)];
        var written = Zstandard.Compress(data, dest, encoder);
        Assert.Equal(data, BclDecompress(dest.AsSpan(0, written).ToArray()));

        // too small destination is reported as an exception, not a partial frame
        Assert.Throws<ZstandardException>(() => Zstandard.Compress(data, new byte[8], encoder));

        // the encoder is still healthy afterwards
        encoder.Reset();
        Assert.Equal(data, BclDecompress(Zstandard.Compress(data, encoder)));
    }

    [Fact]
    public void Decompress_ToSpan()
    {
        var data = SampleData();
        var compressed = Zstandard.Compress(data);

        var dest = new byte[data.Length];
        var written = Zstandard.Decompress(compressed, dest);
        Assert.Equal(data.Length, written);
        Assert.Equal(data, dest);

        // exact size is fine, one byte less is not
        Assert.Throws<ZstandardException>(() => Zstandard.Decompress(compressed, new byte[data.Length - 1]));
        Assert.Throws<ZstandardException>(() => Zstandard.Decompress(new byte[] { 1, 2, 3, 4 }, dest));
    }

    [Fact]
    public void Decompress_ToSpan_WithDictionaryOptions()
    {
        var data = SampleData();
        using var dict = ZstandardDictionary.Create(Utf8(string.Concat(Enumerable.Repeat("zstd native compression dotnet static api ", 32))));
        var compressed = Zstandard.Compress(data, ZstandardCompressionOptions.Default with { Dictionary = dict });
        var options = ZstandardDecompressionOptions.Default with { Dictionary = dict };

        var dest = new byte[data.Length];
        Assert.Equal(data.Length, Zstandard.Decompress(compressed, dest, options));
        Assert.Equal(data, dest);

        Assert.Throws<ZstandardException>(() => Zstandard.Decompress(compressed, dest)); // without dictionary
        Assert.Throws<ZstandardException>(() => Zstandard.Decompress(compressed, new byte[16], options));
    }

    [Fact]
    public void Decompress_ArrayOverloads_TrustedAndUntrusted()
    {
        var data = SampleData();
        using var dict = ZstandardDictionary.Create(Utf8(string.Concat(Enumerable.Repeat("zstd native compression dotnet static api ", 32))));

        var plain = Zstandard.Compress(data);
        Assert.Equal(data, Zstandard.Decompress(plain, trustedData: false));
        Assert.Equal(data, Zstandard.Decompress(plain, trustedData: true));

        var withDict = Zstandard.Compress(data, ZstandardCompressionOptions.Default with { Dictionary = dict });
        var options = ZstandardDecompressionOptions.Default with { Dictionary = dict };
        Assert.Equal(data, Zstandard.Decompress(withDict, options, trustedData: false));
        Assert.Equal(data, Zstandard.Decompress(withDict, options, trustedData: true));

        Assert.Throws<ZstandardException>(() => Zstandard.Decompress(new byte[] { 1, 2, 3, 4 }));
        Assert.Throws<ZstandardException>(() => Zstandard.Decompress(new byte[] { 1, 2, 3, 4 }, trustedData: true));
    }

    [Fact]
    public void Decompress_Array_DecodesAllFrames_LikeStreamAndSpan()
    {
        var a = SampleData();
        var b = Utf8("second frame");
        var empty = Zstandard.Compress(ReadOnlySpan<byte>.Empty);
        var concatenated = Zstandard.Compress(a).Concat(empty).Concat(Zstandard.Compress(b, ZstandardCompressionOptions.Default with { ChecksumFlag = true })).ToArray();
        var expected = a.Concat(b).ToArray();

        Assert.Equal(expected, Zstandard.Decompress(concatenated));
        Assert.Equal(expected, Zstandard.Decompress(concatenated, ZstandardDecompressionOptions.Default));

        // span API and stream agree
        var dest = new byte[expected.Length];
        Assert.Equal(expected.Length, Zstandard.Decompress(concatenated, dest));
        using var zs = new ZstandardStream(new MemoryStream(concatenated), CompressionMode.Decompress);
        var ms = new MemoryStream();
        zs.CopyTo(ms);
        Assert.Equal(expected, ms.ToArray());

        // trusted mode allocates from the bound over every frame, so it decodes all of them too
        Assert.Equal(expected, Zstandard.Decompress(concatenated, trustedData: true));
    }

    [Fact]
    public void Decompress_Array_RejectsTruncatedAndTrailingGarbage()
    {
        var compressed = Zstandard.Compress(SampleData());

        Assert.Throws<ZstandardException>(() => Zstandard.Decompress(compressed.AsSpan(0, compressed.Length / 2)));
        Assert.Throws<ZstandardException>(() => Zstandard.Decompress(compressed.Concat(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).ToArray()));
    }

    [Fact]
    public void Decompress_Array_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(Zstandard.Decompress(ReadOnlySpan<byte>.Empty));
        Assert.Empty(Zstandard.Decompress(ReadOnlySpan<byte>.Empty, trustedData: true));
        Assert.Empty(Zstandard.Decompress(ReadOnlySpan<byte>.Empty, ZstandardDecompressionOptions.Default));
    }

    [Fact]
    public void Decompress_EmptyFrame()
    {
        var empty = Zstandard.Compress(ReadOnlySpan<byte>.Empty);
        Assert.True(empty.Length > 0);

        Assert.Empty(Zstandard.Decompress(empty));
        Assert.Empty(Zstandard.Decompress(empty, trustedData: true));
        Assert.Equal(0, Zstandard.Decompress(empty, Span<byte>.Empty));
        Assert.Empty(BclDecompress(empty));
    }

    // ---- frame inspection

    [Fact]
    public void TryGetFrameContentSize_Cases()
    {
        var data = SampleData();

        Assert.True(Zstandard.TryGetFrameContentSize(Zstandard.Compress(data), out var size));
        Assert.Equal((ulong)data.Length, size);

        // ContentSizeFlag = false removes the size from the header even for one-shot compression
        var noSize = Zstandard.Compress(data, ZstandardCompressionOptions.Default with { ContentSizeFlag = false });
        Assert.False(Zstandard.TryGetFrameContentSize(noSize, out _));
        Assert.Equal(data, Zstandard.Decompress(noSize));
        Assert.Equal(data, Zstandard.Decompress(noSize, trustedData: true)); // falls back to streaming

        // garbage is an error, not "unknown"
        Assert.Throws<ZstandardException>(() => Zstandard.TryGetFrameContentSize(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, out _));
    }

    [Fact]
    public void DictIDFlag_ControlsDictionaryIdInFrameHeader()
    {
        // a trained dictionary carries an id, raw content does not
        var rand = new Random(3);
        var samples = new List<byte[]>();
        for (int i = 0; i < 256; i++)
        {
            var sb = new StringBuilder();
            for (int j = 0; j < 64; j++) sb.Append("key").Append(rand.Next(50)).Append('=').Append(rand.Next(1000)).Append(';');
            samples.Add(Utf8(sb.ToString()));
        }
        using var dict = ZstandardDictionary.Train(samples.SelectMany(x => x).ToArray(), samples.Select(x => x.Length).ToArray(), 8 * 1024);
        Assert.NotEqual(0u, dict.DictionaryId);

        var data = samples[0].Concat(samples[1]).ToArray();

        var withId = Zstandard.Compress(data, ZstandardCompressionOptions.Default with { Dictionary = dict });
        Assert.Equal(dict.DictionaryId, GetDictIdFromFrame(withId));

        var withoutId = Zstandard.Compress(data, ZstandardCompressionOptions.Default with { Dictionary = dict, DictIDFlag = false });
        Assert.Equal(0u, GetDictIdFromFrame(withoutId));

        var options = ZstandardDecompressionOptions.Default with { Dictionary = dict };
        Assert.Equal(data, Zstandard.Decompress(withId, options));
        Assert.Equal(data, Zstandard.Decompress(withoutId, options));

        static unsafe uint GetDictIdFromFrame(byte[] frame)
        {
            fixed (byte* p = frame)
            {
                return ZstandardNativeMethods.ZSTD_getDictID_fromFrame(p, (nuint)frame.Length);
            }
        }
    }

    // ---- exception

    [Fact]
    public void ZstandardException_CarriesNativeErrorName()
    {
        var thrown = Assert.Throws<ZstandardException>(() => Zstandard.Compress(SampleData(), new byte[1]));
        Assert.Contains("too small", thrown.Message);
    }
}
