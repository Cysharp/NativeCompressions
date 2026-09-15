using System.Text;
using BclCompressionOptions = System.IO.Compression.ZstandardCompressionOptions;
using BclDictionary = System.IO.Compression.ZstandardDictionary;
using BclStream = System.IO.Compression.ZstandardStream;
using CompressionLevel = System.IO.Compression.CompressionLevel;
using CompressionMode = System.IO.Compression.CompressionMode;

namespace NativeCompressions.Tests;

// Covers the APIs added for parity with System.IO.Compression in .NET 11:
// ZstandardDictionary.Create / Data / DictionaryId, Zstandard.TryCompress / TryDecompress,
// ZstandardStream(CompressionLevel), ZstandardStream(mode, dictionary), ZstandardStream.BaseStream.
public class ZstandardApiParityTest
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] SampleData() => Utf8(string.Concat(Enumerable.Repeat("zstd native compression dotnet parity sample ", 2000)));

    static byte[] DictionaryBytes() => Utf8(string.Concat(Enumerable.Repeat("zstd native compression dotnet parity dictionary ", 64)));

    static byte[] BclDecompress(byte[] compressed, BclDictionary? dictionary = null)
    {
        var source = new MemoryStream(compressed);
        using var zs = dictionary == null
            ? new BclStream(source, CompressionMode.Decompress)
            : new BclStream(source, CompressionMode.Decompress, dictionary);
        var ms = new MemoryStream();
        zs.CopyTo(ms);
        return ms.ToArray();
    }

    // ---- ZstandardDictionary

    [Fact]
    public void Dictionary_Create_ExposesDataAndLevel()
    {
        var bytes = DictionaryBytes();
        using var dict = ZstandardDictionary.Create(bytes, 5);

        Assert.Equal(bytes, dict.Data.ToArray());
        Assert.Equal(5, dict.CompressionLevel);
        Assert.Equal(0u, dict.DictionaryId); // raw content dictionary has no header
        Assert.False(dict.IsDisposed);
    }

    [Fact]
    public void Dictionary_Create_CopiesInput()
    {
        var bytes = DictionaryBytes();
        using var dict = ZstandardDictionary.Create(bytes);
        bytes[0] ^= 0xFF;
        Assert.NotEqual(bytes[0], dict.Data.Span[0]);
    }

    [Fact]
    public void Dictionary_Create_RejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() => ZstandardDictionary.Create(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Dictionary_DoubleDisposeAndUseAfterDispose()
    {
        var dict = ZstandardDictionary.Create(DictionaryBytes());
        dict.Dispose();
        dict.Dispose();
        Assert.True(dict.IsDisposed);

        var options = ZstandardCompressionOptions.Default with { Dictionary = dict };
        Assert.Throws<ObjectDisposedException>(() => new ZstandardEncoder(options));
        Assert.Throws<ObjectDisposedException>(() => Zstandard.Compress(SampleData(), options));

        var decompressionOptions = ZstandardDecompressionOptions.Default with { Dictionary = dict };
        Assert.Throws<ObjectDisposedException>(() => new ZstandardDecoder(decompressionOptions));
    }

    [Fact]
    public void Dictionary_TrainedByBcl_HasDictionaryIdAndInteroperates()
    {
        // build a trained dictionary with the BCL, then load the same bytes on the native side
        var rand = new Random(7);
        var samples = new List<byte[]>();
        for (int i = 0; i < 256; i++)
        {
            var words = new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta" };
            var sb = new StringBuilder();
            for (int j = 0; j < 128; j++) sb.Append(words[rand.Next(words.Length)]).Append(' ').Append(rand.Next(100));
            samples.Add(Utf8(sb.ToString()));
        }
        var concatenated = samples.SelectMany(x => x).ToArray();
        var lengths = samples.Select(x => x.Length).ToArray();

        using var bclDict = BclDictionary.Train(concatenated, lengths, 16 * 1024);
        using var nativeDict = ZstandardDictionary.Create(bclDict.Data.Span, 3);

        Assert.NotEqual(0u, nativeDict.DictionaryId);
        Assert.Equal(bclDict.Data.ToArray(), nativeDict.Data.ToArray());

        var data = samples[0].Concat(samples[1]).ToArray();
        var compressed = Zstandard.Compress(data, ZstandardCompressionOptions.Default with { Dictionary = nativeDict });
        Assert.Equal(data, BclDecompress(compressed, bclDict));
    }

    static (byte[] Concatenated, int[] Lengths, List<byte[]> Samples) TrainingSamples(int seed, int count = 256)
    {
        var rand = new Random(seed);
        var words = new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta" };
        var samples = new List<byte[]>();
        for (int i = 0; i < count; i++)
        {
            var sb = new StringBuilder();
            for (int j = 0; j < 128; j++) sb.Append(words[rand.Next(words.Length)]).Append(' ').Append(rand.Next(100));
            samples.Add(Utf8(sb.ToString()));
        }
        return (samples.SelectMany(x => x).ToArray(), samples.Select(x => x.Length).ToArray(), samples);
    }

    [Fact]
    public void Dictionary_Train_ProducesUsableDictionary()
    {
        var (concatenated, lengths, samples) = TrainingSamples(seed: 11);

        using var trained = ZstandardDictionary.Train(concatenated, lengths, 16 * 1024, compressionLevel: 5);

        Assert.NotEqual(0u, trained.DictionaryId);
        Assert.True(trained.Data.Length > 0 && trained.Data.Length <= 16 * 1024);
        Assert.Equal(5, trained.CompressionLevel);

        var data = samples[0].Concat(samples[1]).Concat(samples[2]).ToArray();
        var withDict = Zstandard.Compress(data, ZstandardCompressionOptions.Default with { Dictionary = trained });
        var withoutDict = Zstandard.Compress(data, 5);
        Assert.True(withDict.Length < withoutDict.Length, "trained dictionary should help on similar data");

        // the same bytes loaded by the BCL must decode the native output
        using var bclDict = BclDictionary.Create(trained.Data.Span);
        Assert.Equal(data, BclDecompress(withDict, bclDict));

        // and the native side must decode BCL output made with the same dictionary
        var bcl = new MemoryStream();
        using (var zs = new BclStream(bcl, new BclCompressionOptions { Dictionary = bclDict }, leaveOpen: true))
        {
            zs.Write(data);
        }
        Assert.Equal(data, Zstandard.Decompress(bcl.ToArray(), ZstandardDecompressionOptions.Default with { Dictionary = trained }));
    }

    [Fact]
    public void Dictionary_Train_MatchesBclTrainingOutput()
    {
        // both wrap ZDICT_trainFromBuffer, so the same samples and size should give the same bytes
        var (concatenated, lengths, _) = TrainingSamples(seed: 13);

        using var native = ZstandardDictionary.Train(concatenated, lengths, 8 * 1024);
        using var bcl = BclDictionary.Train(concatenated, lengths, 8 * 1024);

        Assert.Equal(bcl.Data.ToArray(), native.Data.ToArray());
    }

    [Fact]
    public void Dictionary_Train_ArgumentValidation()
    {
        var (concatenated, lengths, _) = TrainingSamples(seed: 17, count: 8);

        Assert.Throws<ArgumentOutOfRangeException>(() => ZstandardDictionary.Train(concatenated, lengths, 0));
        Assert.Throws<ArgumentException>(() => ZstandardDictionary.Train(concatenated, ReadOnlySpan<int>.Empty, 1024));

        var wrong = lengths.ToArray();
        wrong[0] += 1;
        Assert.Throws<ArgumentException>(() => ZstandardDictionary.Train(concatenated, wrong, 1024));

        var negative = lengths.ToArray();
        negative[0] = -1;
        Assert.Throws<ArgumentException>(() => ZstandardDictionary.Train(concatenated, negative, 1024));
    }

    [Fact]
    public void Dictionary_Train_TooFewSamplesThrowsZstandardException()
    {
        var sample = Utf8("tiny");
        Assert.Throws<ZstandardException>(() => ZstandardDictionary.Train(sample, new[] { sample.Length }, 1024));
    }

    [Fact]
    public void Dictionary_StaysAliveWhileEncoderUsesIt()
    {
        var data = SampleData();
        var encoder = CreateEncoderAndDropDictionary();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // the encoder must still hold the dictionary, otherwise this would use freed native memory
        var dest = new byte[Zstandard.GetMaxCompressedLength(data.Length)];
        Assert.Equal(System.Buffers.OperationStatus.Done, encoder.Compress(data, dest, out _, out var written, isFinalBlock: true));
        using var bclDict = BclDictionary.Create(DictionaryBytes());
        Assert.Equal(data, BclDecompress(dest.AsSpan(0, written).ToArray(), bclDict));
        encoder.Dispose();

        static ZstandardEncoder CreateEncoderAndDropDictionary()
        {
            var dict = ZstandardDictionary.Create(DictionaryBytes(), 3);
            return new ZstandardEncoder(ZstandardCompressionOptions.Default with { Dictionary = dict });
        }
    }

    // ---- Try one-shot API

    [Fact]
    public void TryCompress_FalseWhenDestinationTooSmall()
    {
        var data = SampleData();
        Assert.False(Zstandard.TryCompress(data, new byte[8], out var written));
        Assert.Equal(0, written);

        var dest = new byte[Zstandard.GetMaxCompressedLength(data.Length)];
        Assert.True(Zstandard.TryCompress(data, dest, out written, 5));
        Assert.Equal(data, BclDecompress(dest.AsSpan(0, written).ToArray()));
    }

    [Fact]
    public void TryCompress_WithOptions()
    {
        var data = SampleData();
        var options = ZstandardCompressionOptions.Default with { CompressionLevel = 7, ChecksumFlag = true };

        Assert.False(Zstandard.TryCompress(data, new byte[8], out var written, options));
        Assert.Equal(0, written);

        var dest = new byte[Zstandard.GetMaxCompressedLength(data.Length)];
        Assert.True(Zstandard.TryCompress(data, dest, out written, options));
        Assert.Equal(data, BclDecompress(dest.AsSpan(0, written).ToArray()));
    }

    [Fact]
    public void TryDecompress_FalseWhenDestinationTooSmall_ThrowsOnCorruptInput()
    {
        var data = SampleData();
        var compressed = Zstandard.Compress(data);

        Assert.False(Zstandard.TryDecompress(compressed, new byte[data.Length / 2], out var written));
        Assert.Equal(0, written);

        var dest = new byte[data.Length];
        Assert.True(Zstandard.TryDecompress(compressed, dest, out written));
        Assert.Equal(data, dest.AsSpan(0, written).ToArray());

        // corrupt data is an error, not "destination too small".
        // zstd only detects payload corruption when the frame carries a checksum, so use one here.
        var withChecksum = Zstandard.Compress(data, ZstandardCompressionOptions.Default with { ChecksumFlag = true });
        var corrupt = withChecksum.ToArray();
        corrupt[corrupt.Length / 2] ^= 0xFF;
        corrupt[corrupt.Length / 2 + 1] ^= 0xFF;
        Assert.Throws<ZstandardException>(() => Zstandard.TryDecompress(corrupt, new byte[data.Length], out _));

        // broken magic number
        var badMagic = compressed.ToArray();
        badMagic[0] ^= 0xFF;
        Assert.Throws<ZstandardException>(() => Zstandard.TryDecompress(badMagic, new byte[data.Length], out _));
        Assert.Throws<ZstandardException>(() => Zstandard.TryDecompress(new byte[] { 1, 2, 3, 4 }, new byte[16], out _));
    }

    [Fact]
    public void TryDecompress_WithDictionaryOptions()
    {
        var data = SampleData();
        using var dict = ZstandardDictionary.Create(DictionaryBytes(), 3);
        var compressed = Zstandard.Compress(data, ZstandardCompressionOptions.Default with { Dictionary = dict });
        var options = ZstandardDecompressionOptions.Default with { Dictionary = dict };

        Assert.False(Zstandard.TryDecompress(compressed, new byte[16], out _, options));

        var dest = new byte[data.Length];
        Assert.True(Zstandard.TryDecompress(compressed, dest, out var written, options));
        Assert.Equal(data, dest.AsSpan(0, written).ToArray());

        // wrong dictionary is an error
        Assert.Throws<ZstandardException>(() => Zstandard.TryDecompress(compressed, new byte[data.Length], out _));
    }

    // ---- ZstandardStream

    [Theory]
    [InlineData(CompressionLevel.Optimal)]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.SmallestSize)]
    public void Stream_CompressionLevelEnum_Interoperates(CompressionLevel level)
    {
        var data = SampleData();

        var ms = new MemoryStream();
        using (var zs = new ZstandardStream(ms, level, leaveOpen: true))
        {
            zs.Write(data);
        }
        Assert.Equal(data, BclDecompress(ms.ToArray()));

        var bcl = new MemoryStream();
        using (var zs = new BclStream(bcl, level, leaveOpen: true))
        {
            zs.Write(data);
        }
        Assert.Equal(data, Zstandard.Decompress(bcl.ToArray()));
    }

    [Fact]
    public void Stream_NoCompression_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new ZstandardStream(new MemoryStream(), CompressionLevel.NoCompression));
    }

    [Fact]
    public void Stream_WithDictionary_Interoperates()
    {
        var data = SampleData();
        var dictBytes = DictionaryBytes();
        using var nativeDict = ZstandardDictionary.Create(dictBytes, 3);
        using var bclDict = BclDictionary.Create(dictBytes);

        // native writes, bcl reads
        var ms = new MemoryStream();
        using (var zs = new ZstandardStream(ms, CompressionMode.Compress, nativeDict, leaveOpen: true))
        {
            zs.Write(data);
        }
        Assert.Equal(data, BclDecompress(ms.ToArray(), bclDict));

        // bcl writes, native reads
        var bcl = new MemoryStream();
        using (var zs = new BclStream(bcl, new BclCompressionOptions { Dictionary = bclDict }, leaveOpen: true))
        {
            zs.Write(data);
        }
        using var reader = new ZstandardStream(new MemoryStream(bcl.ToArray()), CompressionMode.Decompress, nativeDict);
        var result = new MemoryStream();
        reader.CopyTo(result);
        Assert.Equal(data, result.ToArray());
    }

    [Fact]
    public void Stream_WithDictionary_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ZstandardStream(new MemoryStream(), CompressionMode.Compress, (ZstandardDictionary)null!));
    }

    [Fact]
    public void Stream_BaseStream()
    {
        var inner = new MemoryStream();
        var zs = new ZstandardStream(inner, CompressionMode.Compress, leaveOpen: true);
        Assert.Same(inner, zs.BaseStream);

        zs.Dispose();
        Assert.Throws<ObjectDisposedException>(() => zs.BaseStream);
    }
}
