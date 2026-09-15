using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace NativeCompressions.Tests;

// Static members of the LZ4 class: constants, bounds, one-shot APIs, frame inspection, multi frame behavior.
public class LZ4StaticApiTest
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] Compressible(int size, int seed)
    {
        var rand = new Random(seed);
        var words = new[] { "lz4 ", "native ", "compression ", "dotnet ", "static ", "\n" };
        var sb = new StringBuilder(size);
        while (sb.Length < size)
        {
            sb.Append(words[rand.Next(words.Length)]);
            if (rand.Next(50) == 0) sb.Append(rand.Next());
        }
        return Encoding.ASCII.GetBytes(sb.ToString(0, size));
    }

    static readonly byte[] Data = Compressible(300 * 1024, seed: 61);

    static byte[] SkippableFrame(byte[] payload)
    {
        var frame = new byte[8 + payload.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), 0x184D2A50u);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(8));
        return frame;
    }

    static byte[] StreamDecompress(byte[] compressed)
    {
        using var zs = new LZ4Stream(new MemoryStream(compressed), CompressionMode.Decompress);
        var ms = new MemoryStream();
        zs.CopyTo(ms);
        return ms.ToArray();
    }

    // ---- constants

    [Fact]
    public void VersionAndLevels()
    {
        string expected;
        unsafe
        {
            expected = new string((sbyte*)LZ4NativeMethods.LZ4_versionString());
        }
        Assert.Equal(expected, LZ4.Version);
        Assert.Equal(LZ4NativeMethods.LZ4_versionNumber(), LZ4.VersionNumber);
        Assert.Equal(LZ4NativeMethods.LZ4F_getVersion(), LZ4.FrameVersion);

        Assert.Equal(1, LZ4.MinCompressionLevel);
        Assert.Equal(1, LZ4.DefaultCompressionLevel);
        Assert.Equal(LZ4NativeMethods.LZ4F_compressionLevel_max(), LZ4.MaxCompressionLevel);
        Assert.True(LZ4.MaxCompressionLevel >= 12);

        Assert.Equal(5, LZ4.MinSizeToKnowFrameHeaderLength);
        Assert.Equal(19, LZ4.MaxFrameHeaderLength);
        Assert.Equal(8, LZ4.MaxFrameFooterLength);
    }

    // ---- GetMaxCompressedLength (reported: int overload recursed, nuint overload returned 5x)

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(64 * 1024)]
    [InlineData(10 * 1024 * 1024)]
    public void GetMaxCompressedLength_IsSaneAndConsistent(int size)
    {
        var bound = LZ4.GetMaxCompressedLength(size);
        Assert.True(bound >= size + 7 + 4, "bound must cover input plus header and end mark");
        Assert.True(bound <= size + size / 200 + 64 + 4 * 1024 * 1024 / 200 + 1024, "bound must not be wildly larger than the input");

        Assert.Equal(bound, LZ4.GetMaxCompressedLength(size, LZ4CompressionOptions.Default));
        Assert.Equal((nuint)bound, LZ4.GetMaxCompressedLength((nuint)size));
        Assert.Equal((nuint)bound, LZ4.GetMaxCompressedLength((nuint)size, LZ4CompressionOptions.Default));

        // options change the bound only through block size and checksums, still sane
        var withOptions = LZ4.GetMaxCompressedLength(size, LZ4CompressionOptions.Default with { BlockSizeID = BlockSizeId.Max4MB, ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled, BlockChecksumFlag = BlockChecksum.BlockChecksumEnabled });
        Assert.True(withOptions >= size);
        Assert.True(withOptions <= 2 * size + 8 * 1024 * 1024);
    }

    [Fact]
    public void GetMaxCompressedLength_CoversActualOutput()
    {
        foreach (var options in new[]
        {
            LZ4CompressionOptions.Default,
            LZ4CompressionOptions.Default with { ContentSize = 1, ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled, BlockChecksumFlag = BlockChecksum.BlockChecksumEnabled },
            LZ4CompressionOptions.Default with { BlockSizeID = BlockSizeId.Max64KB, BlockMode = BlockMode.BlockIndependent },
        })
        {
            var random = new byte[100_000];
            new Random(7).NextBytes(random); // incompressible, close to the bound
            var dest = new byte[LZ4.GetMaxCompressedLength(random.Length, options)];
            var written = LZ4.Compress(random, dest, options);
            Assert.True(written <= dest.Length);
            Assert.Equal(random, LZ4.Decompress(dest.AsSpan(0, written).ToArray()));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => LZ4.GetMaxCompressedLength(-1));
    }

    // ---- TryGetFrameInfo (reported: threw on non frame data)

    [Fact]
    public void TryGetFrameInfo_ReturnsFalseInsteadOfThrowing()
    {
        Assert.False(LZ4.TryGetFrameInfo(ReadOnlySpan<byte>.Empty, out _));
        Assert.False(LZ4.TryGetFrameInfo(new byte[] { 1, 2, 3 }, out _));
        Assert.False(LZ4.TryGetFrameInfo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, out _));
        Assert.False(LZ4.TryGetFrameInfo(Utf8("not an lz4 frame at all"), out _));

        // zstd magic is not an lz4 frame
        Assert.False(LZ4.TryGetFrameInfo(Zstandard.Compress(Data), out _));

        // a real frame cut inside the header
        var compressed = LZ4.Compress(Data, LZ4CompressionOptions.Default with { ContentSize = 1 });
        Assert.False(LZ4.TryGetFrameInfo(compressed.AsSpan(0, 6), out _));
        Assert.True(LZ4.TryGetFrameInfo(compressed, out var info));
        Assert.Equal(FrameType.Frame, info.FrameType);
        Assert.Equal((ulong)Data.Length, info.ContentSize);
    }

    [Fact]
    public void TryGetFrameInfo_ReportsHeaderFields()
    {
        var options = LZ4CompressionOptions.Default with
        {
            BlockSizeID = BlockSizeId.Max256KB,
            BlockMode = BlockMode.BlockIndependent,
            ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled,
            BlockChecksumFlag = BlockChecksum.BlockChecksumEnabled,
            ContentSize = 1,
        };
        var compressed = LZ4.Compress(Data, options);

        Assert.True(LZ4.TryGetFrameInfo(compressed, out var info));
        Assert.Equal(BlockSizeId.Max256KB, info.BlockSizeID);
        Assert.Equal(BlockMode.BlockIndependent, info.BlockMode);
        Assert.Equal(ContentChecksum.ContentChecksumEnabled, info.ContentChecksumFlag);
        Assert.Equal(BlockChecksum.BlockChecksumEnabled, info.BlockChecksumFlag);
        Assert.Equal((ulong)Data.Length, info.ContentSize);
        Assert.Equal(0u, info.DictionaryID);

        // skippable frames are reported as such
        Assert.True(LZ4.TryGetFrameInfo(SkippableFrame(Utf8("meta")), out var skippable));
        Assert.Equal(FrameType.SkippableFrame, skippable.FrameType);
    }

    // ---- static Compress and ContentSize (reported: span overload always recorded the size)

    [Fact]
    public void Compress_ContentSizeIsRecordedOnlyWhenRequested_BothOverloads()
    {
        var dest = new byte[LZ4.GetMaxCompressedLength(Data.Length)];

        // default: not recorded
        Assert.True(LZ4.TryGetFrameInfo(LZ4.Compress(Data), out var a));
        Assert.Equal(0ul, a.ContentSize);
        var written = LZ4.Compress(Data, dest);
        Assert.True(LZ4.TryGetFrameInfo(dest.AsSpan(0, written).ToArray(), out var b));
        Assert.Equal(0ul, b.ContentSize);

        // any non zero value asks for the real size to be recorded
        var options = LZ4CompressionOptions.Default with { ContentSize = 1 };
        Assert.True(LZ4.TryGetFrameInfo(LZ4.Compress(Data, options), out var c));
        Assert.Equal((ulong)Data.Length, c.ContentSize);
        written = LZ4.Compress(Data, dest, options);
        Assert.True(LZ4.TryGetFrameInfo(dest.AsSpan(0, written).ToArray(), out var d));
        Assert.Equal((ulong)Data.Length, d.ContentSize);

        // both overloads produce identical bytes for identical options
        Assert.Equal(LZ4.Compress(Data, options), dest.AsSpan(0, written).ToArray());
    }

    [Fact]
    public void Compress_ToSpan_TooSmallThrows()
    {
        Assert.Throws<LZ4Exception>(() => LZ4.Compress(Data, new byte[8]));
        Assert.Throws<LZ4Exception>(() => LZ4.Compress(Data, new byte[8], LZ4CompressionOptions.Default with { CompressionLevel = 9 }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(12)]
    public void Compress_Levels(int level)
    {
        var compressed = LZ4.Compress(Data, LZ4CompressionOptions.Default with { CompressionLevel = level });
        Assert.Equal(Data, LZ4.Decompress(compressed));
        Assert.Equal(Data, StreamDecompress(compressed));
    }

    // ---- Decompress: all frames, all paths agree

    [Fact]
    public void Decompress_AllPathsDecodeEveryFrame()
    {
        var a = Data;
        var b = Utf8("second frame");
        var c = Compressible(70_000, 62);
        var concatenated = LZ4.Compress(a)
            .Concat(SkippableFrame(Utf8("skip me")))
            .Concat(LZ4.Compress(b, LZ4CompressionOptions.Default with { ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled, ContentSize = 1 }))
            .Concat(LZ4.Compress(c, LZ4CompressionOptions.Default with { BlockMode = BlockMode.BlockIndependent, BlockSizeID = BlockSizeId.Max64KB }))
            .ToArray();
        var expected = a.Concat(b).Concat(c).ToArray();

        Assert.Equal(expected, LZ4.Decompress(concatenated));
        Assert.Equal(expected, LZ4.Decompress(concatenated, trustedData: true));
        Assert.Equal(expected, LZ4.Decompress(concatenated, LZ4DecompressionOptions.Default));

        var dest = new byte[expected.Length];
        Assert.Equal(expected.Length, LZ4.Decompress(concatenated, dest));
        Assert.Equal(expected, dest);

        Assert.Equal(expected, StreamDecompress(concatenated));
    }

    [Fact]
    public void Decompress_TrustedUsesRecordedSize_AndContinuesWithMoreFrames()
    {
        var first = LZ4.Compress(Data, LZ4CompressionOptions.Default with { ContentSize = 1 });
        Assert.Equal(Data, LZ4.Decompress(first, trustedData: true));

        var second = LZ4.Compress(Utf8("tail"));
        Assert.Equal(Data.Concat(Utf8("tail")).ToArray(), LZ4.Decompress(first.Concat(second).ToArray(), trustedData: true));

        // a lying header is rejected rather than trusted blindly
        var lying = first.ToArray();
        Assert.True(LZ4.TryGetFrameInfo(lying, out var info));
        // content size field starts after magic(4) + FLG(1) + BD(1)
        BitConverter.TryWriteBytes(lying.AsSpan(6, 8), (ulong)Data.Length + 10);
        Assert.Throws<LZ4Exception>(() => LZ4.Decompress(lying, trustedData: true));
    }

    [Fact]
    public void Decompress_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(LZ4.Decompress(ReadOnlySpan<byte>.Empty));
        Assert.Empty(LZ4.Decompress(ReadOnlySpan<byte>.Empty, trustedData: true));
        Assert.Equal(0, LZ4.Decompress(ReadOnlySpan<byte>.Empty, new byte[16]));
        Assert.Empty(StreamDecompress([]));
    }

    [Fact]
    public void Decompress_EmptyFrame()
    {
        var empty = LZ4.Compress(ReadOnlySpan<byte>.Empty);
        Assert.True(empty.Length > 0);
        Assert.Empty(LZ4.Decompress(empty));
        Assert.Equal(0, LZ4.Decompress(empty, Span<byte>.Empty));
        Assert.Empty(StreamDecompress(empty));
    }

    [Fact]
    public void Decompress_RejectsTruncatedCorruptAndTrailingGarbage()
    {
        var compressed = LZ4.Compress(Data, LZ4CompressionOptions.Default with { ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled });
        var truncated = compressed.AsSpan(0, compressed.Length / 2).ToArray();
        var garbage = compressed.Concat(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).ToArray();
        var corrupt = compressed.ToArray();
        corrupt[corrupt.Length / 2] ^= 0xFF;

        foreach (var bad in new[] { truncated, garbage, corrupt, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } })
        {
            Assert.Throws<LZ4Exception>(() => LZ4.Decompress(bad));
            Assert.Throws<LZ4Exception>(() => LZ4.Decompress(bad, new byte[Data.Length + 1024]));
        }

        Assert.Throws<LZ4Exception>(() => LZ4.Decompress(compressed, new byte[Data.Length - 1]));
    }

    [Fact]
    public void Decompress_SkipChecksumsOption()
    {
        var compressed = LZ4.Compress(Data, LZ4CompressionOptions.Default with { ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled });
        var corruptChecksum = compressed.ToArray();
        corruptChecksum[^1] ^= 0xFF;

        Assert.Throws<LZ4Exception>(() => LZ4.Decompress(corruptChecksum));
        Assert.Equal(Data, LZ4.Decompress(corruptChecksum, LZ4DecompressionOptions.Default with { SkipChecksums = true }));
    }

    // ---- block API

    [Fact]
    public void Block_RoundTrip()
    {
        var dest = new byte[LZ4.Block.GetMaxCompressedLength(Data.Length)];
        var written = LZ4.Block.Compress(Data, dest);
        Assert.True(written > 0 && written < Data.Length);

        var result = new byte[Data.Length];
        Assert.Equal(Data.Length, LZ4.Block.Decompress(dest.AsSpan(0, written), result));
        Assert.Equal(Data, result);
    }
}
