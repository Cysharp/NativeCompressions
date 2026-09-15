using System.Text;

namespace NativeCompressions.Fuzz;

/// <summary>
/// Seed inputs per target. Small valid inputs give the fuzzer a head start over random bytes.
/// </summary>
public static class Corpus
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] Text(int repeat) => Utf8(string.Concat(Enumerable.Repeat("zstd native compression dotnet fuzz seed ", repeat)));

    static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new System.Random(seed).NextBytes(bytes);
        return bytes;
    }

    static byte[] Streamed(byte[] data, ZstandardCompressionOptions options)
    {
        using var encoder = new ZstandardEncoder(options);
        var ms = new MemoryStream();
        var output = new byte[4096];
        var half = data.Length / 2;
        Feed(encoder, data.AsSpan(0, half), ms, output, false);
        Feed(encoder, data.AsSpan(half), ms, output, true);
        return ms.ToArray();

        static void Feed(ZstandardEncoder encoder, ReadOnlySpan<byte> piece, MemoryStream ms, byte[] output, bool isFinal)
        {
            System.Buffers.OperationStatus status;
            do
            {
                status = encoder.Compress(piece, output, out var consumed, out var written, isFinal);
                ms.Write(output, 0, written);
                piece = piece.Slice(consumed);
            } while (status == System.Buffers.OperationStatus.DestinationTooSmall || piece.Length > 0);
        }
    }

    static byte[] SkippableFrame(byte[] payload)
    {
        // magic 0x184D2A50, little endian size, payload
        var frame = new byte[8 + payload.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), 0x184D2A50u);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(8));
        return frame;
    }

    static IEnumerable<(string Name, byte[] Data)> Frames()
    {
        var text = Text(20);
        var random = Random(3000, 1);
        var d = ZstandardCompressionOptions.Default;

        yield return ("empty-frame", Zstandard.Compress(ReadOnlySpan<byte>.Empty));
        yield return ("text", Zstandard.Compress(text));
        yield return ("text-checksum", Zstandard.Compress(text, d with { ChecksumFlag = true }));
        yield return ("text-nosize", Zstandard.Compress(text, d with { ContentSizeFlag = false }));
        yield return ("text-level19", Zstandard.Compress(text, 19));
        yield return ("text-level-5", Zstandard.Compress(text, -5));
        yield return ("random", Zstandard.Compress(random));
        yield return ("streamed-nosize", Streamed(text, d));
        yield return ("streamed-window10", Streamed(text, d with { WindowLog = 10, ChecksumFlag = true }));
        yield return ("multi-frame", Zstandard.Compress(text).Concat(Zstandard.Compress(random)).Concat(Zstandard.Compress(ReadOnlySpan<byte>.Empty)).ToArray());
        yield return ("skippable-then-frame", SkippableFrame(Utf8("meta")).Concat(Zstandard.Compress(text)).ToArray());

        using var dict = ZstandardDictionary.Create(Utf8("zstd native compression dotnet fuzz seed dictionary"));
        yield return ("with-dictionary", Zstandard.Compress(text, d with { Dictionary = dict }));

        var truncated = Zstandard.Compress(random);
        yield return ("truncated", truncated.AsSpan(0, truncated.Length / 2).ToArray());
        yield return ("garbage", Random(64, 2));
    }

    static IEnumerable<(string Name, byte[] Data)> Payloads()
    {
        // first bytes are interpreted as options by the roundtrip / dictionary / train targets
        yield return ("empty", new byte[] { 3, 0, 1, 1 });
        yield return ("text", new byte[] { 8, 1, 4, 8 }.Concat(Text(30)).ToArray());
        yield return ("random", new byte[] { 12, 3, 17, 3 }.Concat(Random(2000, 3)).ToArray());
        yield return ("repetitive", new byte[] { 24, 7, 200, 250 }.Concat(Enumerable.Repeat((byte)'a', 5000)).ToArray());
        yield return ("samples", new byte[] { 16, 40 }.Concat(Text(200)).ToArray());
    }

    static IEnumerable<(string Name, byte[] Data)> LZ4Frames()
    {
        var text = Text(20);
        var random = Random(3000, 1);
        var d = LZ4CompressionOptions.Default;

        yield return ("empty-frame", LZ4.Compress(ReadOnlySpan<byte>.Empty));
        yield return ("text", LZ4.Compress(text));
        yield return ("text-checksums", LZ4.Compress(text, d with { ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled, BlockChecksumFlag = BlockChecksum.BlockChecksumEnabled }));
        yield return ("text-size", LZ4.Compress(text, d with { ContentSize = 1 }));
        yield return ("text-level9", LZ4.Compress(text, d with { CompressionLevel = 9 }));
        yield return ("random-independent", LZ4.Compress(random, d with { BlockMode = BlockMode.BlockIndependent, BlockSizeID = BlockSizeId.Max64KB }));
        yield return ("multi-frame", LZ4.Compress(text).Concat(LZ4.Compress(random)).Concat(LZ4.Compress(ReadOnlySpan<byte>.Empty)).ToArray());
        yield return ("skippable-then-frame", SkippableFrame(Utf8("meta")).Concat(LZ4.Compress(text)).ToArray());

        var multiBlock = Text(3000); // several 64KB blocks
        yield return ("multi-block-independent", LZ4.Compress(multiBlock, d with { BlockMode = BlockMode.BlockIndependent, BlockSizeID = BlockSizeId.Max64KB, BlockChecksumFlag = BlockChecksum.BlockChecksumEnabled }));
        yield return ("multi-block-linked", LZ4.Compress(multiBlock, d with { BlockSizeID = BlockSizeId.Max64KB }));

        using var dict = LZ4Dictionary.Create(Utf8("lz4 native compression dotnet fuzz seed dictionary"), 5);
        yield return ("with-dictionary", LZ4.Compress(text, d with { Dictionary = dict }));

        var truncated = LZ4.Compress(random);
        yield return ("truncated", truncated.AsSpan(0, truncated.Length / 2).ToArray());
        yield return ("garbage", Random(64, 2));
    }

    public static IEnumerable<(string Name, byte[] Data)> For(string target) => target.ToLowerInvariant() switch
    {
        "decompress" or "decoder" or "stream" or "decompress-async" => Frames(),
        "roundtrip" or "dictionary" or "train" => Payloads(),
        "lz4-decompress" or "lz4-decoder" or "lz4-stream" or "lz4-decompress-async" => LZ4Frames(),
        "lz4-roundtrip" or "lz4-dictionary" => Payloads(),
        _ => throw new ArgumentException($"unknown target: {target}", nameof(target))
    };

    public static void Write(string directory)
    {
        foreach (var target in FuzzTargets.All.Keys)
        {
            var dir = Path.Combine(directory, target);
            Directory.CreateDirectory(dir);
            foreach (var (name, data) in For(target))
            {
                File.WriteAllBytes(Path.Combine(dir, name), data);
            }
        }
    }
}
