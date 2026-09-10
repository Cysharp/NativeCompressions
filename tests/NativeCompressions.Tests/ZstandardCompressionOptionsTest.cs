using System.Buffers;
using System.Text;
using BclStream = System.IO.Compression.ZstandardStream;
using CompressionMode = System.IO.Compression.CompressionMode;

namespace NativeCompressions.Tests;

// Every tunable in ZstandardCompressionOptions is applied to the native context and must produce
// frames that both the native decoder and the BCL accept.
public class ZstandardCompressionOptionsTest
{
    static byte[] Compressible(int size, int seed)
    {
        var rand = new Random(seed);
        var words = new[] { "zstd ", "native ", "compression ", "dotnet ", "options ", "\n" };
        var sb = new StringBuilder(size);
        while (sb.Length < size)
        {
            sb.Append(words[rand.Next(words.Length)]);
            if (rand.Next(50) == 0) sb.Append(rand.Next());
        }
        return Encoding.ASCII.GetBytes(sb.ToString(0, size));
    }

    static readonly byte[] Data = Compressible(1024 * 1024, seed: 5);

    static byte[] BclDecompress(byte[] compressed)
    {
        using var zs = new BclStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var ms = new MemoryStream();
        zs.CopyTo(ms);
        return ms.ToArray();
    }

    static byte[] CompressStreaming(ZstandardEncoder encoder, byte[] data)
    {
        var ms = new MemoryStream();
        var output = new byte[16 * 1024];
        var remaining = data.AsSpan();
        while (remaining.Length > 0)
        {
            var chunk = remaining.Slice(0, Math.Min(4096, remaining.Length));
            OperationStatus status;
            do
            {
                status = encoder.Compress(chunk, output, out var consumed, out var written, isFinalBlock: false);
                Assert.NotEqual(OperationStatus.InvalidData, status);
                ms.Write(output, 0, written);
                chunk = chunk.Slice(consumed);
            } while (status == OperationStatus.DestinationTooSmall || chunk.Length > 0);
            remaining = remaining.Slice(Math.Min(4096, remaining.Length));
        }

        OperationStatus close;
        do
        {
            close = encoder.Close(output, out var written);
            Assert.NotEqual(OperationStatus.InvalidData, close);
            ms.Write(output, 0, written);
        } while (close == OperationStatus.DestinationTooSmall);

        return ms.ToArray();
    }

    public static IEnumerable<object[]> ParameterNames() => new[]
    {
        "CompressionLevel", "WindowLog", "HashLog", "ChainLog", "SearchLog", "MinMatch", "TargetLength",
        "Strategy.fast", "Strategy.greedy", "Strategy.lazy2", "Strategy.btultra2",
        "EnableLongDistanceMatching", "LdmHashLog", "LdmMinMatch", "LdmBucketSizeLog", "LdmHashRateLog",
        "ContentSizeFlag", "ChecksumFlag", "DictIDFlag", "NbWorkers", "JobSize", "OverlapLog", "Combined",
    }.Select(x => new object[] { x });

    static ZstandardCompressionOptions Build(string name)
    {
        var d = ZstandardCompressionOptions.Default;
        return name switch
        {
            "CompressionLevel" => d with { CompressionLevel = 12 },
            "WindowLog" => d with { WindowLog = 15 },
            "HashLog" => d with { HashLog = 12 },
            "ChainLog" => d with { ChainLog = 12 },
            "SearchLog" => d with { SearchLog = 3 },
            "MinMatch" => d with { MinMatch = 4 },
            "TargetLength" => d with { TargetLength = 64 },
            "Strategy.fast" => d with { Strategy = 1 },
            "Strategy.greedy" => d with { Strategy = 3 },
            "Strategy.lazy2" => d with { Strategy = 5 },
            "Strategy.btultra2" => d with { Strategy = 9 },
            "EnableLongDistanceMatching" => d with { EnableLongDistanceMatching = true },
            "LdmHashLog" => d with { EnableLongDistanceMatching = true, LdmHashLog = 20 },
            "LdmMinMatch" => d with { EnableLongDistanceMatching = true, LdmMinMatch = 32 },
            "LdmBucketSizeLog" => d with { EnableLongDistanceMatching = true, LdmBucketSizeLog = 3 },
            "LdmHashRateLog" => d with { EnableLongDistanceMatching = true, LdmHashRateLog = 4 },
            "ContentSizeFlag" => d with { ContentSizeFlag = false },
            "ChecksumFlag" => d with { ChecksumFlag = true },
            "DictIDFlag" => d with { DictIDFlag = false },
            "NbWorkers" => d with { NbWorkers = 2 },
            "JobSize" => d with { NbWorkers = 2, JobSize = 512 * 1024 },
            "OverlapLog" => d with { NbWorkers = 2, OverlapLog = 3 },
            "Combined" => d with { CompressionLevel = 7, WindowLog = 20, ChecksumFlag = true, EnableLongDistanceMatching = true, NbWorkers = 2 },
            _ => throw new ArgumentException(name)
        };
    }

    [Theory]
    [MemberData(nameof(ParameterNames))]
    public void Parameter_OneShot_DecodesEverywhere(string name)
    {
        var options = Build(name);
        Assert.False(options.IsDefault);

        var compressed = Zstandard.Compress(Data, options);
        Assert.Equal(Data, Zstandard.Decompress(compressed));
        Assert.Equal(Data, BclDecompress(compressed));
    }

    [Theory]
    [MemberData(nameof(ParameterNames))]
    public void Parameter_Streaming_DecodesEverywhere(string name)
    {
        var options = Build(name);

        using var encoder = new ZstandardEncoder(options);
        var compressed = CompressStreaming(encoder, Data);
        Assert.Equal(Data, Zstandard.Decompress(compressed));
        Assert.Equal(Data, BclDecompress(compressed));

        // the same encoder produces a second valid frame
        var again = CompressStreaming(encoder, Data);
        Assert.Equal(Data, BclDecompress(again));
    }

    [Theory]
    [MemberData(nameof(ParameterNames))]
    public void Parameter_Stream_DecodesEverywhere(string name)
    {
        var options = Build(name);

        var ms = new MemoryStream();
        using (var zs = new ZstandardStream(ms, options, leaveOpen: true))
        {
            zs.Write(Data);
        }
        Assert.Equal(Data, BclDecompress(ms.ToArray()));
    }

    [Fact]
    public void ContentSizeFlag_False_OmitsSize()
    {
        var withSize = Zstandard.Compress(Data);
        Assert.True(Zstandard.TryGetFrameContentSize(withSize, out _));

        var withoutSize = Zstandard.Compress(Data, ZstandardCompressionOptions.Default with { ContentSizeFlag = false });
        Assert.False(Zstandard.TryGetFrameContentSize(withoutSize, out _));
    }

    [Fact]
    public void ChecksumFlag_True_DetectsCorruption()
    {
        var plain = Zstandard.Compress(Data);
        var checked_ = Zstandard.Compress(Data, ZstandardCompressionOptions.Default with { ChecksumFlag = true });
        Assert.Equal(plain.Length + 4, checked_.Length); // xxh64 low 32 bits appended

        var corrupt = checked_.ToArray();
        corrupt[corrupt.Length / 2] ^= 0xFF;
        Assert.Throws<ZstandardException>(() => Zstandard.Decompress(corrupt));
    }

    [Fact]
    public void HigherLevelCompressesSmaller()
    {
        var fast = Zstandard.Compress(Data, 1).Length;
        var mid = Zstandard.Compress(Data, 9).Length;
        var best = Zstandard.Compress(Data, 19).Length;
        Assert.True(fast > mid);
        Assert.True(mid >= best);
    }

    [Fact]
    public void NbWorkers_ProducesSameLogicalOutput()
    {
        // multi-threaded output need not be byte identical, but must decode to the same data
        var single = Zstandard.Compress(Data, ZstandardCompressionOptions.Default with { CompressionLevel = 5 });
        var multi = Zstandard.Compress(Data, ZstandardCompressionOptions.Default with { CompressionLevel = 5, NbWorkers = 4 });
        Assert.Equal(Data, Zstandard.Decompress(single));
        Assert.Equal(Data, Zstandard.Decompress(multi));
    }

    [Fact]
    public void IsDefault_AndEquality()
    {
        Assert.True(ZstandardCompressionOptions.Default.IsDefault);
        Assert.True(new ZstandardCompressionOptions().IsDefault);
        Assert.True(new ZstandardCompressionOptions(0).IsDefault);
        Assert.False(new ZstandardCompressionOptions(3).IsDefault);
        Assert.False((ZstandardCompressionOptions.Default with { ChecksumFlag = true }).IsDefault);
        Assert.False((ZstandardCompressionOptions.Default with { ContentSizeFlag = false }).IsDefault);

        var a = ZstandardCompressionOptions.Default with { CompressionLevel = 5, WindowLog = 20 };
        var b = ZstandardCompressionOptions.Default with { CompressionLevel = 5, WindowLog = 20 };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a with { WindowLog = 21 });

        Assert.Equal(3, new ZstandardCompressionOptions(3).CompressionLevel);
        Assert.True(ZstandardCompressionOptions.Default.ContentSizeFlag);
        Assert.False(ZstandardCompressionOptions.Default.ChecksumFlag);
        Assert.True(ZstandardCompressionOptions.Default.DictIDFlag);
    }

    [Theory]
    [InlineData("HashLog")]
    [InlineData("ChainLog")]
    [InlineData("SearchLog")]
    [InlineData("MinMatch")]
    [InlineData("Strategy")]
    [InlineData("LdmHashLog")]
    [InlineData("LdmMinMatch")]
    [InlineData("LdmBucketSizeLog")]
    // JobSize and OverlapLog are clamped by zstd instead of rejected, so they are not listed here.
    public void OutOfRangeParameter_ThrowsAtConstruction(string name)
    {
        var d = ZstandardCompressionOptions.Default;
        var options = name switch
        {
            "HashLog" => d with { HashLog = 99 },
            "ChainLog" => d with { ChainLog = 99 },
            "SearchLog" => d with { SearchLog = 99 },
            "MinMatch" => d with { MinMatch = 1 },
            "Strategy" => d with { Strategy = 42 },
            "LdmHashLog" => d with { EnableLongDistanceMatching = true, LdmHashLog = 99 },
            "LdmMinMatch" => d with { EnableLongDistanceMatching = true, LdmMinMatch = 1 },
            "LdmBucketSizeLog" => d with { EnableLongDistanceMatching = true, LdmBucketSizeLog = 99 },
            _ => throw new ArgumentException(name)
        };

        Assert.Throws<ZstandardException>(() => new ZstandardEncoder(options));
        Assert.Throws<ZstandardException>(() => Zstandard.Compress(Data, options));
        Assert.Throws<ZstandardException>(() => new ZstandardStream(new MemoryStream(), options));
    }

    [Fact]
    public void ResetWithOptions_SwitchesParameters()
    {
        using var encoder = new ZstandardEncoder(1);
        var fast = CompressStreaming(encoder, Data);

        encoder.Reset(ZstandardCompressionOptions.Default with { CompressionLevel = 19, ChecksumFlag = true });
        var best = CompressStreaming(encoder, Data);

        Assert.True(best.Length < fast.Length);
        Assert.Equal(Data, BclDecompress(fast));
        Assert.Equal(Data, BclDecompress(best));

        // back to defaults with the parameterless Reset keeps the level 19 parameters (session only reset)
        encoder.Reset();
        var stillBest = CompressStreaming(encoder, Data);
        Assert.Equal(best.Length, stillBest.Length);
    }

    [Fact]
    public void DecompressionOptions_Equality()
    {
        var a = ZstandardDecompressionOptions.Default with { WindowLogMax = 20 };
        var b = ZstandardDecompressionOptions.Default with { WindowLogMax = 20 };
        Assert.Equal(a, b);
        Assert.NotEqual(a, ZstandardDecompressionOptions.Default);
        Assert.Equal(0, ZstandardDecompressionOptions.Default.WindowLogMax);
        Assert.Null(ZstandardDecompressionOptions.Default.Dictionary);
    }
}
