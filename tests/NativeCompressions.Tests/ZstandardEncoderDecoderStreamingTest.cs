using System.Buffers;
using System.Text;
using BclStream = System.IO.Compression.ZstandardStream;
using CompressionMode = System.IO.Compression.CompressionMode;

namespace NativeCompressions.Tests;

// OperationStatus transitions and edge cases of the streaming ZstandardEncoder / ZstandardDecoder.
public class ZstandardEncoderDecoderStreamingTest
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] Compressible(int size, int seed)
    {
        var rand = new Random(seed);
        var words = new[] { "zstd ", "native ", "compression ", "dotnet ", "streaming ", "\n" };
        var sb = new StringBuilder(size);
        while (sb.Length < size)
        {
            sb.Append(words[rand.Next(words.Length)]);
            if (rand.Next(50) == 0) sb.Append(rand.Next());
        }
        return Encoding.ASCII.GetBytes(sb.ToString(0, size));
    }

    static readonly byte[] Data = Compressible(512 * 1024, seed: 41);

    static byte[] BclDecompress(byte[] compressed)
    {
        using var zs = new BclStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var ms = new MemoryStream();
        zs.CopyTo(ms);
        return ms.ToArray();
    }

    // ---- encoder

    // Incompressible and not a multiple of the 128KB block size, so a partial block stays buffered until Flush / Close.
    static byte[] RandomTail()
    {
        var bytes = new byte[300 * 1024];
        new Random(43).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void Encoder_Close_LoopsUntilDone()
    {
        var data = RandomTail();
        using var encoder = new ZstandardEncoder();
        var big = new byte[Zstandard.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, encoder.Compress(data, big, out var consumed, out var head, isFinalBlock: false));
        Assert.Equal(data.Length, consumed);

        // close with a tiny destination must keep returning DestinationTooSmall and eventually Done
        var ms = new MemoryStream();
        ms.Write(big, 0, head);
        var tiny = new byte[16];
        var loops = 0;
        OperationStatus status;
        do
        {
            status = encoder.Close(tiny, out var written);
            ms.Write(tiny, 0, written);
            loops++;
        } while (status == OperationStatus.DestinationTooSmall);

        Assert.Equal(OperationStatus.Done, status);
        Assert.True(loops > 1);

        // the Compress call above buffered a partial block internally, so the frame is only complete after Close
        Assert.Equal(data, BclDecompress(ms.ToArray()));
    }

    [Fact]
    public void Encoder_Close_OnFreshEncoder_ProducesEmptyFrame()
    {
        using var encoder = new ZstandardEncoder();
        var dest = new byte[64];
        Assert.Equal(OperationStatus.Done, encoder.Close(dest, out var written));
        Assert.True(written > 0);
        Assert.Empty(BclDecompress(dest.AsSpan(0, written).ToArray()));
    }

    [Fact]
    public void Encoder_Flush_LoopsUntilDone()
    {
        var data = RandomTail();
        using var encoder = new ZstandardEncoder();
        var big = new byte[Zstandard.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, encoder.Compress(data, big, out _, out _, isFinalBlock: false));

        var tiny = new byte[16];
        OperationStatus status;
        var loops = 0;
        do
        {
            status = encoder.Flush(tiny, out _);
            loops++;
        } while (status == OperationStatus.DestinationTooSmall);
        Assert.Equal(OperationStatus.Done, status);
        Assert.True(loops > 1);

        // flush on an encoder with nothing buffered is Done with nothing written
        Assert.Equal(OperationStatus.Done, encoder.Flush(tiny, out var written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void Encoder_EmptySource_NonFinal_IsDone()
    {
        using var encoder = new ZstandardEncoder();
        var dest = new byte[64];
        Assert.Equal(OperationStatus.Done, encoder.Compress(ReadOnlySpan<byte>.Empty, dest, out var consumed, out var written, isFinalBlock: false));
        Assert.Equal(0, consumed);
        Assert.Equal(0, written);
    }

    [Fact]
    public void Encoder_DestinationTooSmall_ReportsPartialConsumption()
    {
        using var encoder = new ZstandardEncoder(1);
        var random = new byte[256 * 1024];
        new Random(5).NextBytes(random); // incompressible so output is at least input sized

        var small = new byte[1024];
        var status = encoder.Compress(random, small, out var consumed, out var written, isFinalBlock: true);
        Assert.Equal(OperationStatus.DestinationTooSmall, status);
        Assert.True(written > 0);
        Assert.True(consumed <= random.Length);

        // continuing with the remaining input and enough room finishes the frame
        var ms = new MemoryStream();
        ms.Write(small, 0, written);
        var remaining = random.AsSpan(consumed);
        var big = new byte[Zstandard.GetMaxCompressedLength(random.Length)];
        do
        {
            status = encoder.Compress(remaining, big, out consumed, out written, isFinalBlock: true);
            Assert.NotEqual(OperationStatus.InvalidData, status);
            ms.Write(big, 0, written);
            remaining = remaining.Slice(consumed);
        } while (status != OperationStatus.Done);

        Assert.Equal(random, BclDecompress(ms.ToArray()));
    }

    [Fact]
    public void Encoder_AutoStartsNextFrameAfterFinalBlock()
    {
        using var encoder = new ZstandardEncoder();
        var dest = new byte[Zstandard.GetMaxCompressedLength(Data.Length)];

        Assert.Equal(OperationStatus.Done, encoder.Compress(Data, dest, out _, out var w1, isFinalBlock: true));
        var first = dest.AsSpan(0, w1).ToArray();

        var second = Utf8("second frame without Reset");
        Assert.Equal(OperationStatus.Done, encoder.Compress(second, dest, out _, out var w2, isFinalBlock: true));

        Assert.Equal(Data, BclDecompress(first));
        Assert.Equal(second, BclDecompress(dest.AsSpan(0, w2).ToArray()));
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(1)]
    [InlineData(22)]
    public void Encoder_LevelConstructor(int level)
    {
        using var encoder = new ZstandardEncoder(level);
        var dest = new byte[Zstandard.GetMaxCompressedLength(Data.Length)];
        Assert.Equal(OperationStatus.Done, encoder.Compress(Data, dest, out _, out var written, isFinalBlock: true));
        Assert.Equal(Data, BclDecompress(dest.AsSpan(0, written).ToArray()));
    }

    [Fact]
    public void Encoder_MultiThreaded_StreamingProducesValidFrame()
    {
        // with NbWorkers, Compress returns quickly and data appears on later calls / Close
        using var encoder = new ZstandardEncoder(ZstandardCompressionOptions.Default with { NbWorkers = 2, JobSize = 64 * 1024 });
        var ms = new MemoryStream();
        var output = new byte[8192];

        var remaining = Data.AsSpan();
        while (remaining.Length > 0)
        {
            var chunk = remaining.Slice(0, Math.Min(30_000, remaining.Length));
            OperationStatus status;
            do
            {
                status = encoder.Compress(chunk, output, out var consumed, out var written, isFinalBlock: false);
                Assert.NotEqual(OperationStatus.InvalidData, status);
                ms.Write(output, 0, written);
                chunk = chunk.Slice(consumed);
            } while (status == OperationStatus.DestinationTooSmall || chunk.Length > 0);
            remaining = remaining.Slice(Math.Min(30_000, remaining.Length));
        }

        OperationStatus close;
        do
        {
            close = encoder.Close(output, out var written);
            ms.Write(output, 0, written);
        } while (close == OperationStatus.DestinationTooSmall);
        Assert.Equal(OperationStatus.Done, close);

        Assert.Equal(Data, BclDecompress(ms.ToArray()));
    }

    [Fact]
    public void Encoder_IndependentInstancesInParallel()
    {
        var results = new byte[16][];
        Parallel.For(0, results.Length, i =>
        {
            using var encoder = new ZstandardEncoder(1 + i % 5);
            var dest = new byte[Zstandard.GetMaxCompressedLength(Data.Length)];
            Assert.Equal(OperationStatus.Done, encoder.Compress(Data, dest, out _, out var written, isFinalBlock: true));
            results[i] = dest.AsSpan(0, written).ToArray();
        });

        foreach (var compressed in results)
        {
            Assert.Equal(Data, Zstandard.Decompress(compressed));
        }
    }

    // ---- decoder

    [Fact]
    public void Decoder_Hint_GuidesNextInputSize()
    {
        var compressed = Zstandard.Compress(Data);
        using var decoder = new ZstandardDecoder();
        var dest = new byte[Data.Length];

        // nothing fed yet: needs the header, hint is positive
        var status = decoder.Decompress(ReadOnlySpan<byte>.Empty, dest, out var consumed, out var written, out var hint);
        Assert.Equal(OperationStatus.NeedMoreData, status);
        Assert.Equal(0, consumed);
        Assert.Equal(0, written);
        Assert.True(hint > 0);

        // feed a few header bytes: still positive hint
        status = decoder.Decompress(compressed.AsSpan(0, 5), dest, out consumed, out written, out hint);
        Assert.Equal(OperationStatus.NeedMoreData, status);
        Assert.Equal(5, consumed);
        Assert.True(hint > 0);

        // feed the rest: done, hint is 0
        status = decoder.Decompress(compressed.AsSpan(5), dest, out consumed, out written, out hint);
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(compressed.Length - 5, consumed);
        Assert.Equal(Data.Length, written);
        Assert.Equal(0, hint);
    }

    [Fact]
    public void Decoder_StatusMatrix()
    {
        var compressed = Zstandard.Compress(Data);
        using var decoder = new ZstandardDecoder();

        // source ends mid frame with room left: NeedMoreData
        var dest = new byte[Data.Length];
        var status = decoder.Decompress(compressed.AsSpan(0, compressed.Length / 2), dest, out var consumed, out var written);
        Assert.Equal(OperationStatus.NeedMoreData, status);
        Assert.Equal(compressed.Length / 2, consumed);
        Assert.True(written < Data.Length);
        var total = written;

        // destination is full before the rest is decoded: DestinationTooSmall, consumed all it could
        var remainingSource = compressed.AsSpan(compressed.Length / 2);
        var small = new byte[100];
        status = decoder.Decompress(remainingSource, small, out consumed, out written);
        Assert.Equal(OperationStatus.DestinationTooSmall, status);
        Assert.Equal(100, written);
        Assert.True(consumed > 0);
        Assert.Equal(Data.AsSpan(total, 100).ToArray(), small);
        total += written;
        remainingSource = remainingSource.Slice(consumed);

        // drain everything with a large buffer
        var rest = new byte[Data.Length];
        var ms = new MemoryStream();
        do
        {
            status = decoder.Decompress(remainingSource, rest, out consumed, out written);
            Assert.NotEqual(OperationStatus.InvalidData, status);
            ms.Write(rest, 0, written);
            remainingSource = remainingSource.Slice(consumed);
        } while (status != OperationStatus.Done);

        Assert.Equal(Data.AsSpan(total).ToArray(), ms.ToArray());
    }

    [Fact]
    public void Decoder_InvalidData_OnBadMagic()
    {
        using var decoder = new ZstandardDecoder();
        var dest = new byte[1024];
        var status = decoder.Decompress(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 }, dest, out _, out _, out var hint);
        Assert.Equal(OperationStatus.InvalidData, status);

        // after an error the decoder must be reset before reuse
        decoder.Reset();
        var compressed = Zstandard.Compress(Utf8("ok"));
        Assert.Equal(OperationStatus.Done, decoder.Decompress(compressed, dest, out _, out var written));
        Assert.Equal(Utf8("ok"), dest.AsSpan(0, written).ToArray());
    }

    [Fact]
    public void Decoder_ChecksumMismatch_IsInvalidData()
    {
        var compressed = Zstandard.Compress(Data, ZstandardCompressionOptions.Default with { ChecksumFlag = true });
        compressed[compressed.Length - 1] ^= 0xFF; // corrupt the checksum itself

        using var decoder = new ZstandardDecoder();
        var dest = new byte[Data.Length];
        Assert.Equal(OperationStatus.InvalidData, decoder.Decompress(compressed, dest, out _, out _));
    }

    [Fact]
    public void Decoder_EmptyFrame()
    {
        var empty = Zstandard.Compress(ReadOnlySpan<byte>.Empty);
        using var decoder = new ZstandardDecoder();
        Assert.Equal(OperationStatus.Done, decoder.Decompress(empty, Span<byte>.Empty, out var consumed, out var written));
        Assert.Equal(empty.Length, consumed);
        Assert.Equal(0, written);
    }

    [Fact]
    public void Decoder_ResetWithOptions_SwitchesDictionary()
    {
        using var dict = ZstandardDictionary.Create(Utf8(string.Concat(Enumerable.Repeat("zstd native compression dotnet streaming ", 32))));
        var withDict = Zstandard.Compress(Data, ZstandardCompressionOptions.Default with { Dictionary = dict, ChecksumFlag = true });
        var plain = Zstandard.Compress(Data);

        using var decoder = new ZstandardDecoder();
        var dest = new byte[Data.Length];

        Assert.Equal(OperationStatus.Done, decoder.Decompress(plain, dest, out _, out _));

        decoder.Reset(ZstandardDecompressionOptions.Default with { Dictionary = dict });
        Assert.Equal(OperationStatus.Done, decoder.Decompress(withDict, dest, out _, out var written));
        Assert.Equal(Data, dest.AsSpan(0, written).ToArray());

        // the dictionary sticks across the parameterless Reset
        decoder.Reset();
        Assert.Equal(OperationStatus.Done, decoder.Decompress(withDict, dest, out _, out _));

        // and is dropped by Reset with default options
        decoder.Reset(ZstandardDecompressionOptions.Default);
        Assert.Equal(OperationStatus.InvalidData, decoder.Decompress(withDict, dest, out _, out _));
    }

    [Fact]
    public void Decoder_ByteAtATime()
    {
        var compressed = Zstandard.Compress(Data, ZstandardCompressionOptions.Default with { ChecksumFlag = true });
        using var decoder = new ZstandardDecoder();

        var ms = new MemoryStream();
        var dest = new byte[Data.Length];
        OperationStatus status = OperationStatus.NeedMoreData;
        for (int i = 0; i < compressed.Length; i++)
        {
            status = decoder.Decompress(compressed.AsSpan(i, 1), dest, out var consumed, out var written);
            Assert.NotEqual(OperationStatus.InvalidData, status);
            Assert.Equal(1, consumed);
            ms.Write(dest, 0, written);
        }
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(Data, ms.ToArray());
    }

    [Fact]
    public void Decoder_IndependentInstancesInParallel()
    {
        var compressed = Zstandard.Compress(Data);
        Parallel.For(0, 16, _ =>
        {
            using var decoder = new ZstandardDecoder();
            var dest = new byte[Data.Length];
            Assert.Equal(OperationStatus.Done, decoder.Decompress(compressed, dest, out _, out var written));
            Assert.Equal(Data, dest.AsSpan(0, written).ToArray());
        });
    }
}
