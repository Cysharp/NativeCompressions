using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using BclCompressionOptions = System.IO.Compression.ZstandardCompressionOptions;
using BclDecoder = System.IO.Compression.ZstandardDecoder;
using BclDictionary = System.IO.Compression.ZstandardDictionary;
using BclEncoder = System.IO.Compression.ZstandardEncoder;
using BclStream = System.IO.Compression.ZstandardStream;
using CompressionMode = System.IO.Compression.CompressionMode;

namespace NativeCompressions.Tests;

// Cross-checks NativeCompressions against System.IO.Compression.Zstandard* shipped in .NET 11.
// Data compressed by one side must round-trip through the other.
public class ZstandardBclOracleTest
{
    public static IEnumerable<object[]> Inputs()
    {
        foreach (var level in new[] { 1, 3, 12 })
        {
            yield return new object[] { "empty", level };
            yield return new object[] { "single", level };
            yield return new object[] { "text", level };
            yield return new object[] { "random64k", level };
            yield return new object[] { "random1m", level };
            yield return new object[] { "compressible1m", level };
        }
    }

    static byte[] GetInput(string name) => name switch
    {
        "empty" => [],
        "single" => [42],
        "text" => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("あいうえおかきくけこ The quick brown fox jumps over the lazy dog. ", 200))),
        "random64k" => Random(64 * 1024, seed: 1),
        "random1m" => Random(1024 * 1024, seed: 2),
        "compressible1m" => Compressible(1024 * 1024, seed: 3),
        _ => throw new ArgumentException(name)
    };

    static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    static byte[] Compressible(int size, int seed)
    {
        // runs of repeated words with occasional noise, compresses well but not trivially
        var rand = new Random(seed);
        var words = new[] { "zstd ", "native ", "compression ", "dotnet ", "oracle ", "\n" };
        var sb = new StringBuilder(size);
        while (sb.Length < size)
        {
            sb.Append(words[rand.Next(words.Length)]);
            if (rand.Next(50) == 0) sb.Append(rand.Next());
        }
        return Encoding.ASCII.GetBytes(sb.ToString(0, size));
    }

    // ---- BCL side helpers

    static byte[] BclCompress(byte[] data, int quality, bool appendChecksum = false, BclDictionary? dictionary = null)
    {
        var options = new BclCompressionOptions { Quality = quality, AppendChecksum = appendChecksum };
        if (dictionary != null) options.Dictionary = dictionary;

        var ms = new MemoryStream();
        using (var zs = new BclStream(ms, options, leaveOpen: true))
        {
            zs.Write(data);
        }
        return ms.ToArray();
    }

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

    // ---- NativeCompressions streaming helpers with deliberately small buffers

    static byte[] NativeCompressStreaming(byte[] data, ZstandardEncoder encoder, int inputChunk, int outputChunk)
    {
        var ms = new MemoryStream();
        var output = new byte[outputChunk];
        var remaining = data.AsSpan();

        while (true)
        {
            var isFinal = remaining.Length <= inputChunk;
            var chunk = isFinal ? remaining : remaining.Slice(0, inputChunk);

            OperationStatus status;
            do
            {
                status = encoder.Compress(chunk, output, out var consumed, out var written, isFinal);
                Assert.NotEqual(OperationStatus.InvalidData, status);
                ms.Write(output, 0, written);
                chunk = chunk.Slice(consumed);
            } while (status == OperationStatus.DestinationTooSmall || chunk.Length > 0);

            if (isFinal) break;
            remaining = remaining.Slice(inputChunk);
        }

        return ms.ToArray();
    }

    static byte[] NativeDecompressStreaming(byte[] compressed, ZstandardDecoder decoder, int inputChunk, int outputChunk)
    {
        var ms = new MemoryStream();
        var output = new byte[outputChunk];
        var remaining = compressed.AsSpan();

        while (remaining.Length > 0)
        {
            var chunk = remaining.Slice(0, Math.Min(inputChunk, remaining.Length));
            var status = decoder.Decompress(chunk, output, out var consumed, out var written);
            Assert.NotEqual(OperationStatus.InvalidData, status);
            Assert.True(consumed > 0 || written > 0, "decoder made no progress");

            ms.Write(output, 0, written);
            remaining = remaining.Slice(consumed);

            if (status == OperationStatus.Done)
            {
                decoder.Reset(); // frame finished, more frames may follow
            }
        }

        // drain output still buffered in the native context
        OperationStatus drain;
        do
        {
            drain = decoder.Decompress([], output, out _, out var written);
            ms.Write(output, 0, written);
        } while (drain == OperationStatus.DestinationTooSmall);

        return ms.ToArray();
    }

    static async Task<byte[]> ReadAllAsync(PipeReader reader)
    {
        var ms = new MemoryStream();
        while (true)
        {
            var result = await reader.ReadAsync();
            foreach (var segment in result.Buffer)
            {
                ms.Write(segment.Span);
            }
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) break;
        }
        await reader.CompleteAsync();
        return ms.ToArray();
    }

    // ---- one-shot API

    [Theory]
    [MemberData(nameof(Inputs))]
    public void NativeCompress_BclDecompress(string name, int level)
    {
        var data = GetInput(name);
        var compressed = Zstandard.Compress(data, level);
        Assert.Equal(data, BclDecompress(compressed));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void BclCompress_NativeDecompress(string name, int level)
    {
        var data = GetInput(name);
        var compressed = BclCompress(data, level);
        Assert.Equal(data, Zstandard.Decompress(compressed));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void NativeCompressWithOptions_BclDecompress(string name, int level)
    {
        var data = GetInput(name);
        var options = ZstandardCompressionOptions.Default with { CompressionLevel = level, ChecksumFlag = true };
        var compressed = Zstandard.Compress(data, options);
        Assert.Equal(data, BclDecompress(compressed));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void BclCompressWithChecksum_NativeDecompress(string name, int level)
    {
        var data = GetInput(name);
        var compressed = BclCompress(data, level, appendChecksum: true);
        Assert.Equal(data, Zstandard.Decompress(compressed));
    }

    [Fact]
    public void BclOneShotFrameContentSize_NativeTrustedDecompress()
    {
        var data = GetInput("compressible1m");
        var dest = new byte[BclEncoder.GetMaxCompressedLength(data.Length)];
        Assert.True(BclEncoder.TryCompress(data, dest, out var written));
        var compressed = dest.AsSpan(0, written).ToArray();

        Assert.True(Zstandard.TryGetFrameContentSize(compressed, out var size));
        Assert.Equal((ulong)data.Length, size);

        Assert.Equal(data, Zstandard.Decompress(compressed, trustedData: true));
        Assert.Equal(data, Zstandard.Decompress(compressed, trustedData: false));
    }

    [Fact]
    public void NativeOneShot_BclTryDecompress()
    {
        var data = GetInput("text");
        var compressed = Zstandard.Compress(data);

        Assert.True(BclDecoder.TryGetMaxDecompressedLength(compressed, out var length));
        var dest = new byte[length];
        Assert.True(BclDecoder.TryDecompress(compressed, dest, out var written));
        Assert.Equal(data, dest.AsSpan(0, written).ToArray());
    }

    // ---- streaming encoder / decoder

    [Theory]
    [MemberData(nameof(Inputs))]
    public void NativeStreamingEncoder_BclDecompress(string name, int level)
    {
        var data = GetInput(name);
        using var encoder = new ZstandardEncoder(level);
        var compressed = NativeCompressStreaming(data, encoder, inputChunk: 7919, outputChunk: 1024);
        Assert.Equal(data, BclDecompress(compressed));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void BclCompress_NativeStreamingDecoder(string name, int level)
    {
        var data = GetInput(name);
        var compressed = BclCompress(data, level);
        using var decoder = new ZstandardDecoder();
        var decompressed = NativeDecompressStreaming(compressed, decoder, inputChunk: 61, outputChunk: 1024);
        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void NativeEncoderFlush_BclSeesDataBeforeClose()
    {
        var first = Encoding.UTF8.GetBytes("first part of the message ");
        var second = Encoding.UTF8.GetBytes("and the second part");

        using var encoder = new ZstandardEncoder();
        var output = new byte[4096];
        var ms = new MemoryStream();

        Assert.Equal(OperationStatus.Done, encoder.Compress(first, output, out _, out var w1, isFinalBlock: false));
        ms.Write(output, 0, w1);
        Assert.Equal(OperationStatus.Done, encoder.Flush(output, out var w2));
        ms.Write(output, 0, w2);

        // after Flush, the BCL decoder must be able to produce the first part from an unfinished frame
        using (var partial = new BclDecoder())
        {
            var dest = new byte[4096];
            var status = partial.Decompress(ms.ToArray(), dest, out _, out var written);
            Assert.Equal(OperationStatus.NeedMoreData, status);
            Assert.Equal(first, dest.AsSpan(0, written).ToArray());
        }

        Assert.Equal(OperationStatus.Done, encoder.Compress(second, output, out _, out var w3, isFinalBlock: true));
        ms.Write(output, 0, w3);

        Assert.Equal(first.Concat(second).ToArray(), BclDecompress(ms.ToArray()));
    }

    [Fact]
    public void NativeEncoderReset_MultipleFrames_BclDecompress()
    {
        var a = GetInput("text");
        var b = GetInput("random64k");

        using var encoder = new ZstandardEncoder(3);
        var ms = new MemoryStream();
        ms.Write(NativeCompressStreaming(a, encoder, inputChunk: 4096, outputChunk: 4096));
        encoder.Reset();
        ms.Write(NativeCompressStreaming(b, encoder, inputChunk: 4096, outputChunk: 4096));

        Assert.Equal(a.Concat(b).ToArray(), BclDecompress(ms.ToArray()));
    }

    [Fact]
    public void BclMultipleFrames_NativeStreamingDecoder()
    {
        var a = GetInput("text");
        var b = GetInput("random64k");
        var concatenated = BclCompress(a, 3).Concat(BclCompress(b, 3)).ToArray();

        using var decoder = new ZstandardDecoder();
        var decompressed = NativeDecompressStreaming(concatenated, decoder, inputChunk: 1000, outputChunk: 4096);
        Assert.Equal(a.Concat(b).ToArray(), decompressed);
    }

    // ---- Stream

    [Theory]
    [MemberData(nameof(Inputs))]
    public void NativeStream_BclStream(string name, int level)
    {
        var data = GetInput(name);
        var options = ZstandardCompressionOptions.Default with { CompressionLevel = level };

        var ms = new MemoryStream();
        using (var zs = new ZstandardStream(ms, options, leaveOpen: true))
        {
            // write in odd sized pieces
            var remaining = data.AsSpan();
            while (remaining.Length > 0)
            {
                var n = Math.Min(3331, remaining.Length);
                zs.Write(remaining.Slice(0, n));
                remaining = remaining.Slice(n);
            }
        }

        Assert.Equal(data, BclDecompress(ms.ToArray()));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void BclStream_NativeStream(string name, int level)
    {
        var data = GetInput(name);
        var compressed = BclCompress(data, level);

        using var zs = new ZstandardStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var ms = new MemoryStream();
        var buffer = new byte[1234];
        int read;
        while ((read = zs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, read);
        }

        Assert.Equal(data, ms.ToArray());
    }

    [Fact]
    public async Task NativeStreamAsync_BclStream()
    {
        var data = GetInput("compressible1m");

        var ms = new MemoryStream();
        await using (var zs = new ZstandardStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            await zs.WriteAsync(data);
            await zs.FlushAsync();
        }

        Assert.Equal(data, BclDecompress(ms.ToArray()));
    }

    [Fact]
    public async Task BclStream_NativeStreamAsync()
    {
        var data = GetInput("random1m");
        var compressed = BclCompress(data, 3);

        await using var zs = new ZstandardStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var ms = new MemoryStream();
        await zs.CopyToAsync(ms);

        Assert.Equal(data, ms.ToArray());
    }

    // ---- PipeWriter / PipeReader

    [Theory]
    [InlineData("text")]
    [InlineData("random1m")]
    [InlineData("compressible1m")]
    public async Task NativeCompressAsync_BclDecompress(string name)
    {
        var data = GetInput(name);

        var pipe = new Pipe();
        var reading = ReadAllAsync(pipe.Reader);
        await Zstandard.CompressAsync((ReadOnlyMemory<byte>)data, pipe.Writer);
        await pipe.Writer.CompleteAsync();
        var compressed = await reading;

        Assert.Equal(data, BclDecompress(compressed));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("random1m")]
    [InlineData("compressible1m")]
    public async Task BclCompress_NativeDecompressAsync(string name)
    {
        var data = GetInput(name);
        var compressed = BclCompress(data, 3);

        var pipe = new Pipe();
        var reading = ReadAllAsync(pipe.Reader);
        await Zstandard.DecompressAsync((ReadOnlyMemory<byte>)compressed, pipe.Writer);
        await pipe.Writer.CompleteAsync();
        var decompressed = await reading;

        Assert.Equal(data, decompressed);
    }

    // ---- dictionary

    static byte[] DictionaryBytes()
    {
        // raw content dictionary, shared verbatim by both sides
        return Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("zstd native compression dotnet oracle dictionary sample ", 64)));
    }

    [Fact]
    public void NativeCompressWithDictionary_BclDecompressWithDictionary()
    {
        var data = GetInput("compressible1m");
        var dictBytes = DictionaryBytes();

        using var nativeDict = ZstandardDictionary.Create(dictBytes, 3);
        var options = ZstandardCompressionOptions.Default with { CompressionLevel = 3, Dictionary = nativeDict };
        var compressed = Zstandard.Compress(data, options);

        using var bclDict = BclDictionary.Create(dictBytes);
        Assert.Equal(data, BclDecompress(compressed, bclDict));

        // without the dictionary the BCL must refuse the frame
        Assert.ThrowsAny<Exception>(() => BclDecompress(compressed));
    }

    [Fact]
    public void BclCompressWithDictionary_NativeDecompressWithDictionary()
    {
        var data = GetInput("compressible1m");
        var dictBytes = DictionaryBytes();

        using var bclDict = BclDictionary.Create(dictBytes);
        var compressed = BclCompress(data, 3, dictionary: bclDict);

        using var nativeDict = ZstandardDictionary.Create(dictBytes, 3);
        var options = ZstandardDecompressionOptions.Default with { Dictionary = nativeDict };
        Assert.Equal(data, Zstandard.Decompress(compressed, options));

        // without the dictionary the native decoder must refuse the frame
        Assert.ThrowsAny<Exception>(() => Zstandard.Decompress(compressed));
    }
}
