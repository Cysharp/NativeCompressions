using System.IO.Compression;
using System.Text;

namespace NativeCompressions.Tests;

// Every LZ4CompressionOptions field reaches the frame and every path decodes the result.
public class LZ4CompressionOptionsTest
{
    static byte[] Compressible(int size, int seed)
    {
        var rand = new Random(seed);
        var words = new[] { "lz4 ", "native ", "compression ", "dotnet ", "options ", "\n" };
        var sb = new StringBuilder(size);
        while (sb.Length < size)
        {
            sb.Append(words[rand.Next(words.Length)]);
            if (rand.Next(50) == 0) sb.Append(rand.Next());
        }
        return Encoding.ASCII.GetBytes(sb.ToString(0, size));
    }

    static readonly byte[] Data = Compressible(1024 * 1024 + 12345, seed: 71);

    static byte[] StreamCompress(byte[] data, in LZ4CompressionOptions options)
    {
        var ms = new MemoryStream();
        using (var zs = new LZ4Stream(ms, options, leaveOpen: true))
        {
            zs.Write(data);
        }
        return ms.ToArray();
    }

    static byte[] StreamDecompress(byte[] compressed)
    {
        using var zs = new LZ4Stream(new MemoryStream(compressed), CompressionMode.Decompress);
        var ms = new MemoryStream();
        zs.CopyTo(ms);
        return ms.ToArray();
    }

    static byte[] EncoderCompress(byte[] data, in LZ4CompressionOptions options, int chunk)
    {
        using var encoder = new LZ4Encoder(options);
        var ms = new MemoryStream();
        var buffer = new byte[encoder.GetMaxCompressedLength(chunk)];
        var remaining = data.AsSpan();
        while (remaining.Length > 0)
        {
            var piece = remaining.Slice(0, Math.Min(chunk, remaining.Length));
            ms.Write(buffer, 0, encoder.Compress(piece, buffer));
            remaining = remaining.Slice(piece.Length);
        }
        ms.Write(buffer, 0, encoder.Close(buffer));
        return ms.ToArray();
    }

    public static IEnumerable<object[]> ParameterNames() => new[]
    {
        "CompressionLevel.3", "CompressionLevel.Max",
        "BlockSizeID.64KB", "BlockSizeID.256KB", "BlockSizeID.1MB", "BlockSizeID.4MB",
        "BlockMode.Independent", "ContentChecksum", "BlockChecksum", "ContentSize", "FavorDecompressionSpeed", "AutoFlush", "Combined",
    }.Select(x => new object[] { x });

    static LZ4CompressionOptions Build(string name)
    {
        var d = LZ4CompressionOptions.Default;
        return name switch
        {
            "CompressionLevel.3" => d with { CompressionLevel = 3 },
            "CompressionLevel.Max" => d with { CompressionLevel = LZ4.MaxCompressionLevel },
            "BlockSizeID.64KB" => d with { BlockSizeID = BlockSizeId.Max64KB },
            "BlockSizeID.256KB" => d with { BlockSizeID = BlockSizeId.Max256KB },
            "BlockSizeID.1MB" => d with { BlockSizeID = BlockSizeId.Max1MB },
            "BlockSizeID.4MB" => d with { BlockSizeID = BlockSizeId.Max4MB },
            "BlockMode.Independent" => d with { BlockMode = BlockMode.BlockIndependent },
            "ContentChecksum" => d with { ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled },
            "BlockChecksum" => d with { BlockChecksumFlag = BlockChecksum.BlockChecksumEnabled },
            "ContentSize" => d with { ContentSize = (ulong)Data.Length },
            "FavorDecompressionSpeed" => d with { CompressionLevel = 10, FavorDecompressionSpeed = 1 },
            "AutoFlush" => d with { AutoFlush = true },
            "Combined" => d with { CompressionLevel = 6, BlockSizeID = BlockSizeId.Max256KB, BlockMode = BlockMode.BlockIndependent, ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled, BlockChecksumFlag = BlockChecksum.BlockChecksumEnabled, ContentSize = (ulong)Data.Length },
            _ => throw new ArgumentException(name)
        };
    }

    [Theory]
    [MemberData(nameof(ParameterNames))]
    public void Parameter_OneShot(string name)
    {
        var options = Build(name);
        var compressed = LZ4.Compress(Data, options);

        Assert.Equal(Data, LZ4.Decompress(compressed));
        Assert.Equal(Data, StreamDecompress(compressed));

        Assert.True(LZ4.TryGetFrameInfo(compressed, out var info));
        if (options.BlockSizeID != BlockSizeId.Default) Assert.Equal(options.BlockSizeID, info.BlockSizeID);
        // LZ4F_compressFrame switches a single block frame to independent mode, so linked is only guaranteed for multi block input
        if (options.BlockMode == BlockMode.BlockIndependent || options.BlockSizeID == BlockSizeId.Max64KB) Assert.Equal(options.BlockMode, info.BlockMode);
        Assert.Equal(options.ContentChecksumFlag, info.ContentChecksumFlag);
        Assert.Equal(options.BlockChecksumFlag, info.BlockChecksumFlag);
        Assert.Equal(options.ContentSize == 0 ? 0ul : (ulong)Data.Length, info.ContentSize);
    }

    [Theory]
    [MemberData(nameof(ParameterNames))]
    public void Parameter_Encoder(string name)
    {
        var options = Build(name);
        var compressed = EncoderCompress(Data, options, chunk: 100_003);
        Assert.Equal(Data, LZ4.Decompress(compressed));
        Assert.Equal(Data, StreamDecompress(compressed));
    }

    [Theory]
    [MemberData(nameof(ParameterNames))]
    public void Parameter_Stream(string name)
    {
        var options = Build(name);
        if (options.ContentSize != 0) return; // LZ4Stream cannot promise a total size up front

        var compressed = StreamCompress(Data, options);
        Assert.Equal(Data, LZ4.Decompress(compressed));
        Assert.Equal(Data, StreamDecompress(compressed));
    }

    [Fact]
    public void ContentSize_MustMatchInStreaming()
    {
        using var encoder = new LZ4Encoder(LZ4CompressionOptions.Default with { ContentSize = (ulong)Data.Length + 1 });
        var buffer = new byte[encoder.GetMaxCompressedLength(Data.Length)];
        var written = encoder.Compress(Data, buffer);
        Assert.Throws<LZ4Exception>(() => encoder.Close(buffer.AsSpan(written)));
    }

    [Fact]
    public void HigherLevelCompressesSmaller()
    {
        var fast = LZ4.Compress(Data, LZ4CompressionOptions.Default with { CompressionLevel = 1 }).Length;
        var high = LZ4.Compress(Data, LZ4CompressionOptions.Default with { CompressionLevel = 9 }).Length;
        Assert.True(high < fast);
    }

    [Fact]
    public void Dictionary_SetsDictionaryID()
    {
        using var dict = LZ4Dictionary.Create(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("lz4 native compression dotnet options ", 32))), 7);
        var options = LZ4CompressionOptions.Default with { Dictionary = dict };
        Assert.Equal(7u, options.DictionaryID);
        Assert.Same(dict, options.Dictionary);

        var withoutDictionary = options with { Dictionary = null };
        Assert.Equal(0u, withoutDictionary.DictionaryID);
    }

    [Fact]
    public void Equality()
    {
        var a = LZ4CompressionOptions.Default with { CompressionLevel = 5, BlockSizeID = BlockSizeId.Max1MB };
        var b = LZ4CompressionOptions.Default with { CompressionLevel = 5, BlockSizeID = BlockSizeId.Max1MB };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a with { CompressionLevel = 6 });

        var d1 = LZ4DecompressionOptions.Default with { SkipChecksums = true };
        var d2 = LZ4DecompressionOptions.Default with { SkipChecksums = true };
        Assert.Equal(d1, d2);
        Assert.NotEqual(d1, LZ4DecompressionOptions.Default);
        Assert.False(LZ4DecompressionOptions.Default.StableDst);
    }

    [Fact]
    public void DecompressionOptions_StableDst()
    {
        var compressed = LZ4.Compress(Data, LZ4CompressionOptions.Default with { BlockMode = BlockMode.BlockLinked });
        var options = LZ4DecompressionOptions.Default with { StableDst = true };

        // one contiguous destination satisfies stableDst
        var dest = new byte[Data.Length];
        Assert.Equal(Data.Length, LZ4.Decompress(compressed, dest, options));
        Assert.Equal(Data, dest);
    }
}
