using System.Buffers;
using System.IO.Pipelines;
using BclDecoder = System.IO.Compression.ZstandardDecoder;
using BclDictionary = System.IO.Compression.ZstandardDictionary;
using BclEncoder = System.IO.Compression.ZstandardEncoder;
using CompressionMode = System.IO.Compression.CompressionMode;

namespace NativeCompressions.Fuzz;

public delegate void FuzzTarget(ReadOnlySpan<byte> data);

/// <summary>
/// Thrown when a target observes a broken invariant. Anything else that escapes a target is also a finding.
/// </summary>
public sealed class FuzzAssertionException(string message) : Exception(message);

/// <summary>
/// Fuzz targets for the Zstandard binding. Each target takes arbitrary bytes and must either finish
/// or throw one of the exceptions the API documents. Native crashes, hangs, unexpected exception
/// types and broken invariants are findings.
/// </summary>
public static class FuzzTargets
{
    /// <summary>
    /// Fuzz input is small but zstd can expand it enormously (RLE blocks), so output is capped.
    /// </summary>
    public const int MaxOutput = 8 * 1024 * 1024;

    public static readonly IReadOnlyDictionary<string, FuzzTarget> All = new Dictionary<string, FuzzTarget>(StringComparer.OrdinalIgnoreCase)
    {
        ["decompress"] = Decompress,
        ["decoder"] = Decoder,
        ["stream"] = Stream,
        ["decompress-async"] = DecompressAsync,
        ["roundtrip"] = RoundTrip,
        ["dictionary"] = Dictionary,
        ["train"] = Train,
        ["lz4-decompress"] = LZ4Decompress,
        ["lz4-decoder"] = LZ4DecoderTarget,
        ["lz4-stream"] = LZ4StreamTarget,
        ["lz4-decompress-async"] = LZ4DecompressAsync,
        ["lz4-roundtrip"] = LZ4RoundTrip,
        ["lz4-dictionary"] = LZ4DictionaryTarget,
    };

    // ---- LZ4: one-shot and frame inspection, every path must agree

    public static void LZ4Decompress(ReadOnlySpan<byte> data)
    {
        // frame inspection never throws
        var hasInfo = LZ4.TryGetFrameInfo(data, out var info);
        if (hasInfo && info.FrameType == FrameType.Frame && info.ContentSize > MaxOutput) return; // header claims a huge frame

        byte[]? untrusted = null;
        try { untrusted = LZ4.Decompress(data); }
        catch (LZ4Exception) { }
        catch (OutOfMemoryException) { return; } // a small input can legally expand far beyond the cap

        if (untrusted != null && untrusted.Length > MaxOutput) return;

        byte[]? trusted = null;
        try { trusted = LZ4.Decompress(data, trustedData: true); } catch (LZ4Exception) { }
        if (untrusted != null && trusted != null)
        {
            Check(untrusted.AsSpan().SequenceEqual(trusted), "trusted and untrusted decompression differ");
        }

        if (untrusted != null)
        {
            var dest = new byte[untrusted.Length];
            var written = LZ4.Decompress(data, dest);
            Check(written == untrusted.Length && dest.AsSpan().SequenceEqual(untrusted), "span and array decompression differ");

            // stream agrees
            using var zs = new LZ4Stream(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
            var ms = new MemoryStream();
            zs.CopyTo(ms);
            Check(ms.ToArray().AsSpan().SequenceEqual(untrusted), "stream and array decompression differ");
        }
    }

    public static void LZ4DecoderTarget(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        var inputChunk = 1 + (data[0] % 64) * 16;
        var outputChunk = 1 + (data[^1] % 32) * 128;

        using var decoder = new LZ4Decoder();
        var output = new byte[outputChunk];
        long total = 0;
        var remaining = data;
        var status = OperationStatus.NeedMoreData;
        var noProgress = 0;

        while (remaining.Length > 0)
        {
            var piece = remaining.Slice(0, Math.Min(inputChunk, remaining.Length));
            status = decoder.Decompress(piece, output, out var consumed, out var written, out var hint);

            Check(consumed >= 0 && consumed <= piece.Length, "consumed out of range");
            Check(written >= 0 && written <= output.Length, "written out of range");
            Check(hint >= 0, "negative hint");

            total += written;
            remaining = remaining.Slice(consumed);

            if (status == OperationStatus.InvalidData) return;
            if (status == OperationStatus.Done)
            {
                decoder.Reset();
                noProgress = 0;
                continue;
            }

            if (consumed == 0 && written == 0)
            {
                Check(++noProgress < 3, "decoder made no progress");
            }
            else
            {
                noProgress = 0;
            }

            if (total > MaxOutput) return;
        }

        var drains = 0;
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = decoder.Decompress(ReadOnlySpan<byte>.Empty, output, out _, out var written);
            total += written;
            Check(written > 0 || status != OperationStatus.DestinationTooSmall, "drain made no progress");
            Check(++drains < 1_000_000, "drain does not terminate");
            if (status == OperationStatus.Done) decoder.Reset();
            if (total > MaxOutput) return;
        }
    }

    public static void LZ4StreamTarget(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        var maxRead = 1 + data[0] % 200;
        var bufferSize = 1 + data[^1] % 4096;

        using var zs = new LZ4Stream(new TrickleStream(data.ToArray(), maxRead), CompressionMode.Decompress);
        var buffer = new byte[bufferSize];
        long total = 0;
        try
        {
            int read;
            while ((read = zs.Read(buffer, 0, buffer.Length)) > 0)
            {
                Check(read <= buffer.Length, "read out of range");
                total += read;
                if (total > MaxOutput) return;
            }
            Check(zs.Read(buffer, 0, buffer.Length) == 0, "stream did not stay at EOF");
        }
        catch (InvalidOperationException)
        {
            // invalid data
        }
    }

    public static void LZ4DecompressAsync(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        if (LZ4.TryGetFrameInfo(data, out var info) && info.FrameType == FrameType.Frame && info.ContentSize > MaxOutput) return;

        byte[]? expected = null;
        try { expected = LZ4.Decompress(data); }
        catch (LZ4Exception) { }
        catch (OutOfMemoryException) { return; }
        if (expected != null && expected.Length > MaxOutput) return;

        var segmentSize = 1 + (data[0] % 64) * 32;
        var sequence = ToSequence(data.ToArray(), segmentSize);

        foreach (var dop in new[] { 1, 2 })
        {
            byte[]? actual = null;
            try
            {
                actual = Collect(w => LZ4.DecompressAsync(sequence, w, maxDegreeOfParallelism: dop));
            }
            catch (LZ4Exception) { }

            if (expected != null)
            {
                Check(actual != null, $"DecompressAsync(dop {dop}) rejected input that Decompress accepted");
                Check(expected.AsSpan().SequenceEqual(actual), $"DecompressAsync(dop {dop}) differs from one-shot");
            }
            else
            {
                Check(actual == null, $"DecompressAsync(dop {dop}) accepted input that Decompress rejected");
            }
        }
    }

    public static void LZ4RoundTrip(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return;

        var level = 1 + data[0] % LZ4.MaxCompressionLevel;
        var blockSize = (data[1] & 3) switch { 0 => BlockSizeId.Max64KB, 1 => BlockSizeId.Max256KB, 2 => BlockSizeId.Max1MB, _ => BlockSizeId.Max4MB };
        var independent = (data[1] & 4) != 0;
        var contentChecksum = (data[1] & 8) != 0;
        var blockChecksum = (data[1] & 16) != 0;
        var contentSize = (data[1] & 32) != 0;
        var inputChunk = 1 + data[2] * 8;
        var payload = data.Slice(4);

        var options = LZ4CompressionOptions.Default with
        {
            CompressionLevel = level,
            BlockSizeID = blockSize,
            BlockMode = independent ? BlockMode.BlockIndependent : BlockMode.BlockLinked,
            ContentChecksumFlag = contentChecksum ? ContentChecksum.ContentChecksumEnabled : ContentChecksum.NoContentChecksum,
            BlockChecksumFlag = blockChecksum ? BlockChecksum.BlockChecksumEnabled : BlockChecksum.NoBlockChecksum,
            ContentSize = contentSize ? 1ul : 0ul,
        };

        // one-shot
        var compressed = LZ4.Compress(payload, options);
        Check(compressed.Length <= LZ4.GetMaxCompressedLength(payload.Length, options), "compressed exceeds GetMaxCompressedLength");
        Check(LZ4.Decompress(compressed).AsSpan().SequenceEqual(payload), "one-shot round trip differs");
        Check(LZ4.Decompress(compressed, trustedData: true).AsSpan().SequenceEqual(payload), "trusted round trip differs");
        Check(LZ4.TryGetFrameInfo(compressed, out var info), "own frame not recognized");
        Check(info.ContentSize == (contentSize ? (ulong)payload.Length : 0ul), "content size not as requested");

        // streaming encoder, chunked
        using var encoder = new LZ4Encoder(options with { ContentSize = contentSize ? (ulong)payload.Length : 0ul });
        var ms = new MemoryStream();
        var buffer = new byte[encoder.GetMaxCompressedLength(inputChunk)];
        var remaining = payload;
        while (remaining.Length > 0)
        {
            var piece = remaining.Slice(0, Math.Min(inputChunk, remaining.Length));
            var written = encoder.Compress(piece, buffer);
            Check(written <= buffer.Length, "encoder wrote past the bound");
            ms.Write(buffer, 0, written);
            remaining = remaining.Slice(piece.Length);
        }
        ms.Write(buffer, 0, encoder.Close(buffer));
        var streamed = ms.ToArray();
        Check(LZ4.Decompress(streamed).AsSpan().SequenceEqual(payload), "streamed round trip differs");

        // LZ4Stream both ways
        var stream = new MemoryStream();
        using (var zs = new LZ4Stream(stream, options with { ContentSize = 0 }, leaveOpen: true))
        {
            zs.Write(payload);
        }
        using (var reader = new LZ4Stream(new TrickleStream(stream.ToArray(), 1 + data[3] % 100), CompressionMode.Decompress))
        {
            var result = new MemoryStream();
            reader.CopyTo(result);
            Check(result.ToArray().AsSpan().SequenceEqual(payload), "LZ4Stream round trip differs");
        }

        // async, sequential and parallel
        foreach (var dop in new[] { 1, 2 })
        {
            var viaAsync = Collect(w => LZ4.DecompressAsync(ToSequence(compressed, 1000), w, maxDegreeOfParallelism: dop));
            Check(viaAsync.AsSpan().SequenceEqual(payload), $"DecompressAsync(dop {dop}) round trip differs");
        }
    }

    public static void LZ4DictionaryTarget(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return;
        var split = 1 + (data[0] * (data.Length - 2)) / 256;
        var dictBytes = data.Slice(1, split);
        var payload = data.Slice(1 + split);
        var id = data[1];

        using var dict = LZ4Dictionary.Create(dictBytes, id);
        Check(dict.Data.Span.SequenceEqual(dictBytes), "dictionary data not preserved");
        Check(dict.DictionaryId == id, "dictionary id not preserved");

        var compressed = LZ4.Compress(payload, LZ4CompressionOptions.Default with { Dictionary = dict, ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled });
        var options = LZ4DecompressionOptions.Default with { Dictionary = dict };
        Check(LZ4.Decompress(compressed, options).AsSpan().SequenceEqual(payload), "dictionary round trip differs");
        Check(LZ4.TryGetFrameInfo(compressed, out var info) && info.DictionaryID == id, "dictionary id not in frame");

        try
        {
            var without = LZ4.Decompress(compressed);
            Check(without.AsSpan().SequenceEqual(payload), "decoding without dictionary produced different bytes without an error");
        }
        catch (LZ4Exception) { }

        using var decoder = new LZ4Decoder(options);
        var dest = new byte[payload.Length];
        var status = decoder.Decompress(compressed, dest, out _, out var w);
        Check(status == OperationStatus.Done && w == payload.Length && dest.AsSpan().SequenceEqual(payload), "streaming dictionary decode differs");
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new FuzzAssertionException(message);
    }

    // ---- one-shot decompression, plus BCL as an oracle

    public static void Decompress(ReadOnlySpan<byte> data)
    {
        // frame inspection never crashes
        var hasContentSize = false;
        ulong contentSize = 0;
        try
        {
            hasContentSize = Zstandard.TryGetFrameContentSize(data, out contentSize);
        }
        catch (ZstandardException) { }

        var hasBound = Zstandard.TryGetMaxDecompressedLength(data, out var bound);
        if (hasBound && hasContentSize)
        {
            Check((ulong)bound >= contentSize, "bound is smaller than the first frame content size");
        }

        if (!hasBound || bound > MaxOutput)
        {
            // header claims a huge output, or the bound is unknown: only streaming with a cap is safe
            DecodeCapped(data, chunk: 4096, outputChunk: 64 * 1024);
            return;
        }

        byte[]? untrusted = null;
        try { untrusted = Zstandard.Decompress(data); } catch (ZstandardException) { }

        byte[]? trusted = null;
        try { trusted = Zstandard.Decompress(data, trustedData: true); } catch (ZstandardException) { }

        if (untrusted != null && trusted != null)
        {
            Check(untrusted.AsSpan().SequenceEqual(trusted), "trusted and untrusted decompression differ");
        }

        // span API with the bound as capacity, ZSTD_decompress decodes every frame so compare the first frame prefix
        var dest = new byte[bound];
        var ok = false;
        var written = 0;
        try { ok = Zstandard.TryDecompress(data, dest, out written); } catch (ZstandardException) { }
        if (ok)
        {
            Check(written <= bound, "wrote more than the bound");
            if (untrusted != null)
            {
                Check(written >= untrusted.Length && dest.AsSpan(0, untrusted.Length).SequenceEqual(untrusted), "span and array decompression differ");
            }
        }

        // BCL oracle, only compared when both sides succeed
        if (ok)
        {
            var bclDest = new byte[bound];
            if (BclDecoder.TryDecompress(data, bclDest, out var bclWritten))
            {
                Check(bclWritten == written && bclDest.AsSpan(0, bclWritten).SequenceEqual(dest.AsSpan(0, written)), "BCL and native decompression differ");
            }
        }
    }

    // ---- streaming decoder with input and output chunking chosen by the data

    public static void Decoder(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        var inputChunk = 1 + (data[0] % 64) * 16;
        var outputChunk = 1 + (data[^1] % 32) * 128;
        DecodeCapped(data, inputChunk, outputChunk);
    }

    static long DecodeCapped(ReadOnlySpan<byte> data, int chunk, int outputChunk)
    {
        using var decoder = new ZstandardDecoder();
        var output = new byte[outputChunk];
        long total = 0;
        var remaining = data;
        var status = OperationStatus.NeedMoreData;
        var noProgress = 0;

        while (remaining.Length > 0)
        {
            var piece = remaining.Slice(0, Math.Min(chunk, remaining.Length));
            status = decoder.Decompress(piece, output, out var consumed, out var written, out var hint);

            Check(consumed >= 0 && consumed <= piece.Length, "consumed out of range");
            Check(written >= 0 && written <= output.Length, "written out of range");
            Check(hint >= 0, "negative hint");

            total += written;
            remaining = remaining.Slice(consumed);

            if (status == OperationStatus.InvalidData) return total;
            if (status == OperationStatus.Done)
            {
                Check(hint == 0, "Done must report hint 0");
                decoder.Reset();
                noProgress = 0;
                continue;
            }

            if (consumed == 0 && written == 0)
            {
                Check(++noProgress < 3, "decoder made no progress");
            }
            else
            {
                noProgress = 0;
            }

            if (total > MaxOutput) return total;
        }

        var drains = 0;
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = decoder.Decompress(ReadOnlySpan<byte>.Empty, output, out _, out var written);
            total += written;
            Check(written > 0 || status != OperationStatus.DestinationTooSmall, "drain made no progress");
            Check(++drains < 1_000_000, "drain does not terminate");
            if (status == OperationStatus.Done) decoder.Reset();
            if (total > MaxOutput) return total;
        }

        return total;
    }

    // ---- ZstandardStream over an inner stream that returns small reads

    public static void Stream(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        var maxRead = 1 + data[0] % 200;
        var bufferSize = 1 + data[^1] % 4096;

        using var zs = new ZstandardStream(new TrickleStream(data.ToArray(), maxRead), CompressionMode.Decompress);
        var buffer = new byte[bufferSize];
        long total = 0;
        try
        {
            int read;
            while ((read = zs.Read(buffer, 0, buffer.Length)) > 0)
            {
                Check(read <= buffer.Length, "read out of range");
                total += read;
                if (total > MaxOutput) return;
            }
            Check(zs.Read(buffer, 0, buffer.Length) == 0, "stream did not stay at EOF");
        }
        catch (InvalidOperationException)
        {
            // invalid data
        }
    }

    // ---- DecompressAsync over a multi segment sequence

    public static void DecompressAsync(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        if (!Zstandard.TryGetMaxDecompressedLength(data, out var bound) || bound > MaxOutput) return;

        var segmentSize = 1 + (data[0] % 64) * 32;
        var sequence = ToSequence(data.ToArray(), segmentSize);

        byte[]? expected = null;
        try
        {
            var all = new byte[bound];
            if (Zstandard.TryDecompress(data, all, out var written)) expected = all.AsSpan(0, written).ToArray();
        }
        catch (ZstandardException) { }

        byte[]? actual = null;
        try
        {
            actual = Collect(w => Zstandard.DecompressAsync(sequence, w));
        }
        catch (ZstandardException) { }

        if (expected != null && actual != null)
        {
            Check(expected.AsSpan().SequenceEqual(actual), "DecompressAsync differs from one-shot");
        }
        if (expected != null && actual == null)
        {
            // one-shot accepted every frame, so the async path must too
            throw new FuzzAssertionException("DecompressAsync rejected input that ZSTD_decompress accepted");
        }
    }

    // ---- compress then decompress through every path, options taken from the data

    public static void RoundTrip(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return;

        var level = (data[0] % 25) - 5; // -5 .. 19
        var checksum = (data[1] & 1) != 0;
        var contentSize = (data[1] & 2) != 0;
        var windowLog = (data[1] & 4) != 0 ? 10 + data[2] % 8 : 0;
        var inputChunk = 1 + data[2] * 8;
        var outputChunk = 1 + data[3] * 4;
        var payload = data.Slice(4);

        var options = ZstandardCompressionOptions.Default with
        {
            CompressionLevel = level,
            ChecksumFlag = checksum,
            ContentSizeFlag = contentSize,
            WindowLog = windowLog,
        };

        // one-shot
        var compressed = Zstandard.Compress(payload, options);
        Check(compressed.Length <= Zstandard.GetMaxCompressedLength(payload.Length), "compressed exceeds GetMaxCompressedLength");
        Check(Zstandard.Decompress(compressed).AsSpan().SequenceEqual(payload), "one-shot round trip differs");
        Check(Zstandard.Decompress(compressed, trustedData: true).AsSpan().SequenceEqual(payload), "trusted round trip differs");
        if (contentSize)
        {
            Check(Zstandard.TryGetFrameContentSize(compressed, out var size) && size == (ulong)payload.Length, "content size not recorded");
        }
        else
        {
            Check(!Zstandard.TryGetFrameContentSize(compressed, out _), "content size recorded although disabled");
        }

        // BCL decodes native output
        var bclDest = new byte[payload.Length];
        Check(BclDecoder.TryDecompress(compressed, bclDest, out var bclWritten) && bclWritten == payload.Length && bclDest.AsSpan().SequenceEqual(payload), "BCL cannot decode native output");

        // streaming encoder with chunking, streaming decoder with chunking
        using var encoder = new ZstandardEncoder(options);
        var streamed = CompressStreaming(encoder, payload, inputChunk, outputChunk);
        Check(Zstandard.Decompress(streamed).AsSpan().SequenceEqual(payload), "streamed round trip differs");
        Check(DecodeStreaming(streamed, inputChunk, outputChunk).AsSpan().SequenceEqual(payload), "streaming decoder differs");

        // ZstandardStream both ways
        var ms = new MemoryStream();
        using (var zs = new ZstandardStream(ms, options, leaveOpen: true))
        {
            zs.Write(payload);
        }
        using (var reader = new ZstandardStream(new TrickleStream(ms.ToArray(), 1 + data[3] % 100), CompressionMode.Decompress))
        {
            var result = new MemoryStream();
            reader.CopyTo(result);
            Check(result.ToArray().AsSpan().SequenceEqual(payload), "ZstandardStream round trip differs");
        }

        // native decodes BCL output
        var bclOut = new byte[BclEncoder.GetMaxCompressedLength(payload.Length)];
        Check(BclEncoder.TryCompress(payload, bclOut, out var bclLen, Math.Max(level, 1), 0), "BCL compress failed");
        Check(Zstandard.Decompress(bclOut.AsSpan(0, bclLen)).AsSpan().SequenceEqual(payload), "native cannot decode BCL output");
    }

    // ---- raw content dictionaries

    public static void Dictionary(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return;
        var split = 1 + (data[0] * (data.Length - 2)) / 256;
        var dictBytes = data.Slice(1, split);
        var payload = data.Slice(1 + split);
        var level = (data[1] % 12) + 1;

        using var dict = ZstandardDictionary.Create(dictBytes, level);
        Check(dict.Data.Span.SequenceEqual(dictBytes), "dictionary data not preserved");
        Check(dict.CompressionLevel == level, "dictionary level not preserved");

        var compressed = Zstandard.Compress(payload, ZstandardCompressionOptions.Default with { Dictionary = dict, CompressionLevel = level });
        var options = ZstandardDecompressionOptions.Default with { Dictionary = dict };
        Check(Zstandard.Decompress(compressed, options).AsSpan().SequenceEqual(payload), "dictionary round trip differs");
        Check(Zstandard.Decompress(compressed, options, trustedData: true).AsSpan().SequenceEqual(payload), "trusted dictionary round trip differs");

        // decoding without the dictionary either fails or, when the frame never referenced it, gives the same bytes
        try
        {
            var without = Zstandard.Decompress(compressed);
            Check(without.AsSpan().SequenceEqual(payload), "decoding without dictionary produced different bytes without an error");
        }
        catch (ZstandardException) { }

        // BCL with the same raw content dictionary
        using var bclDict = BclDictionary.Create(dictBytes);
        var bclDest = new byte[payload.Length];
        Check(BclDecoder.TryDecompress(compressed, bclDest, out var written, bclDict) && written == payload.Length && bclDest.AsSpan().SequenceEqual(payload), "BCL cannot decode with the same dictionary");

        // streaming with the dictionary via Reset(options)
        using var decoder = new ZstandardDecoder();
        decoder.Reset(options);
        var dest = new byte[payload.Length];
        var status = decoder.Decompress(compressed, dest, out _, out var w);
        Check(status == OperationStatus.Done && w == payload.Length && dest.AsSpan().SequenceEqual(payload), "streaming dictionary decode differs");
    }

    // ---- dictionary training

    public static void Train(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) return;
        var sampleCount = 1 + data[0] % 64;
        var maxDictionarySize = 256 + data[1] * 64;
        var samples = data.Slice(2);

        // split into sampleCount pieces of roughly equal size
        var lengths = new int[sampleCount];
        var each = samples.Length / sampleCount;
        for (int i = 0; i < sampleCount; i++) lengths[i] = each;
        lengths[^1] += samples.Length - each * sampleCount;

        ZstandardDictionary dict;
        try
        {
            dict = ZstandardDictionary.Train(samples, lengths, maxDictionarySize);
        }
        catch (ZstandardException)
        {
            return; // not enough or too small samples
        }
        catch (ArgumentException)
        {
            return; // zero length samples etc.
        }

        using (dict)
        {
            Check(dict.Data.Length > 0 && dict.Data.Length <= maxDictionarySize, "trained dictionary size out of range");
            Check(dict.DictionaryId != 0, "trained dictionary has no id");

            var payload = samples.Slice(0, Math.Min(samples.Length, lengths[0]));
            var compressed = Zstandard.Compress(payload, ZstandardCompressionOptions.Default with { Dictionary = dict });
            Check(Zstandard.Decompress(compressed, ZstandardDecompressionOptions.Default with { Dictionary = dict }).AsSpan().SequenceEqual(payload), "trained dictionary round trip differs");

            using var bclDict = BclDictionary.Create(dict.Data.Span);
            var bclDest = new byte[payload.Length];
            Check(BclDecoder.TryDecompress(compressed, bclDest, out var written, bclDict) && written == payload.Length && bclDest.AsSpan().SequenceEqual(payload), "BCL cannot decode with the trained dictionary");
        }
    }

    // ---- helpers

    static byte[] CompressStreaming(ZstandardEncoder encoder, ReadOnlySpan<byte> data, int inputChunk, int outputChunk)
    {
        var ms = new MemoryStream();
        var output = new byte[outputChunk];
        var remaining = data;
        while (true)
        {
            var isFinal = remaining.Length <= inputChunk;
            var piece = isFinal ? remaining : remaining.Slice(0, inputChunk);
            OperationStatus status;
            var guard = 0;
            do
            {
                status = encoder.Compress(piece, output, out var consumed, out var written, isFinal);
                Check(status != OperationStatus.InvalidData, "encoder returned InvalidData");
                Check(consumed <= piece.Length && written <= output.Length, "encoder counts out of range");
                Check(++guard < 10_000_000, "encoder does not terminate");
                ms.Write(output, 0, written);
                piece = piece.Slice(consumed);
            } while (status == OperationStatus.DestinationTooSmall || piece.Length > 0);

            if (isFinal) break;
            remaining = remaining.Slice(inputChunk);
        }
        return ms.ToArray();
    }

    static byte[] DecodeStreaming(ReadOnlySpan<byte> compressed, int inputChunk, int outputChunk)
    {
        using var decoder = new ZstandardDecoder();
        var ms = new MemoryStream();
        var output = new byte[outputChunk];
        var remaining = compressed;
        var status = OperationStatus.NeedMoreData;
        while (remaining.Length > 0)
        {
            var piece = remaining.Slice(0, Math.Min(inputChunk, remaining.Length));
            status = decoder.Decompress(piece, output, out var consumed, out var written);
            Check(status != OperationStatus.InvalidData, "decoder returned InvalidData on valid data");
            Check(consumed > 0 || written > 0, "decoder made no progress on valid data");
            ms.Write(output, 0, written);
            remaining = remaining.Slice(consumed);
            if (status == OperationStatus.Done) decoder.Reset();
        }
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = decoder.Decompress(ReadOnlySpan<byte>.Empty, output, out _, out var written);
            ms.Write(output, 0, written);
        }
        Check(status == OperationStatus.Done, "valid data did not end with Done");
        return ms.ToArray();
    }

    static byte[] Collect(Func<PipeWriter, ValueTask> producer)
    {
        var pipe = new Pipe();
        var reading = Task.Run(async () =>
        {
            var ms = new MemoryStream();
            while (true)
            {
                var result = await pipe.Reader.ReadAsync();
                foreach (var segment in result.Buffer) ms.Write(segment.Span);
                pipe.Reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted) break;
            }
            await pipe.Reader.CompleteAsync();
            return ms.ToArray();
        });

        try
        {
            producer(pipe.Writer).AsTask().GetAwaiter().GetResult();
            pipe.Writer.Complete();
        }
        catch (Exception ex)
        {
            pipe.Writer.Complete(ex);
            try { reading.GetAwaiter().GetResult(); } catch { }
            throw;
        }
        return reading.GetAwaiter().GetResult();
    }

    static ReadOnlySequence<byte> ToSequence(byte[] data, int segmentSize)
    {
        if (data.Length == 0) return ReadOnlySequence<byte>.Empty;

        Segment? first = null;
        Segment? last = null;
        for (int offset = 0; offset < data.Length; offset += segmentSize)
        {
            var length = Math.Min(segmentSize, data.Length - offset);
            var segment = new Segment(data.AsMemory(offset, length), last == null ? 0 : last.RunningIndex + last.Memory.Length);
            first ??= segment;
            last?.SetNext(segment);
            last = segment;
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public void SetNext(Segment next) => Next = next;
    }

    sealed class TrickleStream(byte[] data, int maxRead) : System.IO.Stream
    {
        int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, maxRead), data.Length - position);
            Array.Copy(data, position, buffer, offset, n);
            position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
