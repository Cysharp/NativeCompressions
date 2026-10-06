using CompressionMode = System.IO.Compression.CompressionMode;
using System.IO.Pipelines;
using NativeCompressions;

namespace BlazorWasm;

// Round trips through every path that reaches the native code: the simple API, Stream, PipeWriter and dictionaries.
// Each line starts with "ok" or "FAIL" so the result can be read from the page or the browser console.
public static class WasmSmoke
{
    public static IReadOnlyList<string> LastResults { get; private set; } = [];

    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var lines = new List<string>();
        var source = SampleText(1_000_000);

        lines.Add($"info: LZ4 {LZ4.Version}, Zstandard {Zstandard.Version}, pointer size {IntPtr.Size} bytes, {Environment.OSVersion}");

        Check(lines, "LZ4.Compress/Decompress", () =>
        {
            var compressed = LZ4.Compress(source);
            return (LZ4.Decompress(compressed), compressed.Length);
        }, source);

        Check(lines, "LZ4 high compression (level 9)", () =>
        {
            var compressed = LZ4.Compress(source, LZ4CompressionOptions.Default with { CompressionLevel = 9 });
            return (LZ4.Decompress(compressed), compressed.Length);
        }, source);

        Check(lines, "LZ4Stream", () =>
        {
            using var buffer = new MemoryStream();
            using (var stream = new LZ4Stream(buffer, CompressionMode.Compress, leaveOpen: true))
            {
                stream.Write(source);
            }
            var length = (int)buffer.Length;
            buffer.Position = 0;
            using var decompress = new LZ4Stream(buffer, CompressionMode.Decompress);
            using var output = new MemoryStream();
            decompress.CopyTo(output);
            return (output.ToArray(), length);
        }, source);

        Check(lines, "LZ4 dictionary", () =>
        {
            var dictionaryBytes = TrainDictionary();
            using var dictionary = LZ4Dictionary.Create(dictionaryBytes);
            var compressed = LZ4.Compress(source, LZ4CompressionOptions.Default with { Dictionary = dictionary });
            return (LZ4.Decompress(compressed, LZ4DecompressionOptions.Default with { Dictionary = dictionary }), compressed.Length);
        }, source);

        Check(lines, "Zstandard.Compress/Decompress", () =>
        {
            var compressed = Zstandard.Compress(source);
            return (Zstandard.Decompress(compressed), compressed.Length);
        }, source);

        Check(lines, "Zstandard level 19", () =>
        {
            var compressed = Zstandard.Compress(source, 19);
            return (Zstandard.Decompress(compressed), compressed.Length);
        }, source);

        Check(lines, "ZstandardStream", () =>
        {
            using var buffer = new MemoryStream();
            using (var stream = new ZstandardStream(buffer, CompressionMode.Compress, leaveOpen: true))
            {
                stream.Write(source);
            }
            var length = (int)buffer.Length;
            buffer.Position = 0;
            using var decompress = new ZstandardStream(buffer, CompressionMode.Decompress);
            using var output = new MemoryStream();
            decompress.CopyTo(output);
            return (output.ToArray(), length);
        }, source);

        Check(lines, "ZstandardDictionary.Train + dictionary round trip", () =>
        {
            var dictionaryBytes = TrainDictionary();
            using var dictionary = ZstandardDictionary.Create(dictionaryBytes);
            var compressed = Zstandard.Compress(source, ZstandardCompressionOptions.Default with { Dictionary = dictionary });
            return (Zstandard.Decompress(compressed, ZstandardDecompressionOptions.Default with { Dictionary = dictionary }), compressed.Length);
        }, source);

        await CheckAsync(lines, "Zstandard.CompressAsync/DecompressAsync (PipeWriter)", async () =>
        {
            var compressedPipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            await Zstandard.CompressAsync(source, compressedPipe.Writer);
            await compressedPipe.Writer.CompleteAsync();
            var compressed = await ReadAllAsync(compressedPipe.Reader);

            var decompressedPipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            await Zstandard.DecompressAsync(compressed, decompressedPipe.Writer);
            await decompressedPipe.Writer.CompleteAsync();
            return (await ReadAllAsync(decompressedPipe.Reader), compressed.Length);
        }, source);

        await CheckAsync(lines, "LZ4.CompressAsync/DecompressAsync (PipeWriter)", async () =>
        {
            var compressedPipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            await LZ4.CompressAsync(source, compressedPipe.Writer);
            await compressedPipe.Writer.CompleteAsync();
            var compressed = await ReadAllAsync(compressedPipe.Reader);

            var decompressedPipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            await LZ4.DecompressAsync(compressed, decompressedPipe.Writer);
            await decompressedPipe.Writer.CompleteAsync();
            return (await ReadAllAsync(decompressedPipe.Reader), compressed.Length);
        }, source);

        Check(lines, "truncated input throws", () =>
        {
            var compressed = Zstandard.Compress(source);
            try
            {
                Zstandard.Decompress(compressed.AsSpan(0, compressed.Length / 2));
                return (Array.Empty<byte>(), 0);
            }
            catch (Exception ex)
            {
                return (source, ex.GetType().Name.Length); // same content means pass, the "size" is just informative
            }
        }, source);

        lines.Add(lines.Any(x => x.StartsWith("FAIL")) ? "RESULT: FAIL" : "RESULT: all ok");
        LastResults = lines;
        return lines;
    }

    static void Check(List<string> lines, string name, Func<(byte[] result, int compressedLength)> action, byte[] expected)
    {
        try
        {
            var (result, compressedLength) = action();
            lines.Add(result.AsSpan().SequenceEqual(expected)
                ? $"ok   {name} ({expected.Length} -> {compressedLength} bytes)"
                : $"FAIL {name}: round trip mismatch ({result.Length} bytes back)");
        }
        catch (Exception ex)
        {
            lines.Add($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static async Task CheckAsync(List<string> lines, string name, Func<Task<(byte[] result, int compressedLength)>> action, byte[] expected)
    {
        try
        {
            var (result, compressedLength) = await action();
            lines.Add(result.AsSpan().SequenceEqual(expected)
                ? $"ok   {name} ({expected.Length} -> {compressedLength} bytes)"
                : $"FAIL {name}: round trip mismatch ({result.Length} bytes back)");
        }
        catch (Exception ex)
        {
            lines.Add($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static async Task<byte[]> ReadAllAsync(PipeReader reader)
    {
        using var output = new MemoryStream();
        while (true)
        {
            var result = await reader.ReadAsync();
            foreach (var segment in result.Buffer)
            {
                output.Write(segment.Span);
            }
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) break;
        }
        await reader.CompleteAsync();
        return output.ToArray();
    }

    // Compressible text with a fixed seed, so every run sees the same bytes.
    static byte[] SampleText(int length)
    {
        var words = new[] { "lz4", "zstandard", "wasm", "browser", "native", "compress", "decompress", "pipe", "stream", "dictionary" };
        var random = new Random(42);
        var builder = new System.Text.StringBuilder(length + 16);
        while (builder.Length < length)
        {
            builder.Append(words[random.Next(words.Length)]).Append(' ').Append(random.Next(1000)).Append('\n');
        }
        return System.Text.Encoding.UTF8.GetBytes(builder.ToString(0, length));
    }

    static byte[] TrainDictionary()
    {
        var random = new Random(7);
        var samples = new List<byte>();
        var lengths = new List<int>();
        for (var i = 0; i < 200; i++)
        {
            var sample = System.Text.Encoding.UTF8.GetBytes($"{{\"id\":{random.Next(100000)},\"name\":\"user{random.Next(1000)}\",\"role\":\"compressor\",\"active\":true}}");
            samples.AddRange(sample);
            lengths.Add(sample.Length);
        }
        return ZstandardDictionary.Train(samples.ToArray(), lengths.ToArray(), 16 * 1024);
    }
}
