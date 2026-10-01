using System.Reflection;
using System.Runtime.InteropServices;

namespace NativeCompressions.Tests;

// LZ4.Decompress(source, destination) calls lz4frame directly instead of going through LZ4Decoder.
// These tests pin its behavior against the array returning overload, which does go through the decoder.
public class LZ4DecompressToSpanTest
{
    static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // half of each block repeats, so blocks compress and reference what came before
    static byte[] Compressible(int size, int seed)
    {
        var bytes = Random(size, seed);
        for (var i = 4096; i < bytes.Length; i += 8192)
        {
            Array.Copy(bytes, i - 4096, bytes, i, Math.Min(4096, bytes.Length - i));
        }
        return bytes;
    }

    static byte[] SkippableFrame(byte[] payload)
    {
        var frame = new byte[8 + payload.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), 0x184D2A50u);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(8));
        return frame;
    }

    static readonly LZ4CompressionOptions[] OptionVariants =
    [
        LZ4CompressionOptions.Default,
        LZ4CompressionOptions.Default with { BlockMode = BlockMode.BlockIndependent, BlockSizeID = BlockSizeId.Max256KB },
        LZ4CompressionOptions.Default with { ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled, BlockChecksumFlag = BlockChecksum.BlockChecksumEnabled },
        LZ4CompressionOptions.Default with { CompressionLevel = 9, AutoFlush = true },
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(65_536)]
    [InlineData(65_537)]
    [InlineData(300_000)]
    [InlineData(5_000_000)]
    public void RoundTrip_ExactAndLargerDestination(int size)
    {
        var data = Compressible(size, 1);
        foreach (var options in OptionVariants)
        {
            var compressed = LZ4.Compress(data, options);

            var exact = new byte[size];
            Assert.Equal(size, LZ4.Decompress(compressed, exact));
            Assert.Equal(data, exact);

            var larger = new byte[size + 1000];
            Assert.Equal(size, LZ4.Decompress(compressed, larger));
            Assert.Equal(data, larger.AsSpan(0, size).ToArray());
        }
    }

    [Fact]
    public void EmptySource_ReturnsZero()
    {
        Assert.Equal(0, LZ4.Decompress(ReadOnlySpan<byte>.Empty, new byte[10]));
        Assert.Equal(0, LZ4.Decompress(ReadOnlySpan<byte>.Empty, Span<byte>.Empty));
    }

    [Fact]
    public void ConcatenatedAndSkippableFrames()
    {
        var a = Compressible(100_000, 2);
        var b = Compressible(70_000, 3);
        var source = LZ4.Compress(a)
            .Concat(SkippableFrame(Random(33, 4)))
            .Concat(LZ4.Compress(Array.Empty<byte>()))
            .Concat(LZ4.Compress(b, OptionVariants[2]))
            .Concat(SkippableFrame([]))
            .ToArray();
        var expected = a.Concat(b).ToArray();

        var destination = new byte[expected.Length];
        Assert.Equal(expected.Length, LZ4.Decompress(source, destination));
        Assert.Equal(expected, destination);
        Assert.Equal(expected, LZ4.Decompress(source));
    }

    [Fact]
    public void DestinationTooSmall_Throws()
    {
        var data = Compressible(200_000, 5);
        var compressed = LZ4.Compress(data);

        foreach (var length in new[] { 0, 1, 1000, 65_535, 65_536, 199_999 })
        {
            var ex = Assert.Throws<LZ4Exception>(() => LZ4.Decompress(compressed, new byte[length]));
            Assert.Equal("Destination buffer is too small.", ex.Message);
        }

        // two frames, room for the first only
        var two = compressed.Concat(compressed).ToArray();
        Assert.Throws<LZ4Exception>(() => LZ4.Decompress(two, new byte[data.Length]));
    }

    [Fact]
    public void TruncatedSource_Throws()
    {
        var data = Compressible(200_000, 6);
        var compressed = LZ4.Compress(data, OptionVariants[2]);
        var destination = new byte[data.Length + 100];

        foreach (var length in new[] { 1, 3, 6, 7, 10, 100, 70_000, compressed.Length - 9, compressed.Length - 4, compressed.Length - 1 })
        {
            Assert.Throws<LZ4Exception>(() => LZ4.Decompress(compressed.AsSpan(0, length), destination));
        }

        // a complete frame followed by the start of another
        var cut = compressed.Concat(compressed.AsSpan(0, 20).ToArray()).ToArray();
        Assert.Throws<LZ4Exception>(() => LZ4.Decompress(cut, new byte[data.Length * 2]));
    }

    [Fact]
    public void InvalidSource_ThrowsWithNativeErrorName()
    {
        var data = Compressible(100_000, 7);
        var destination = new byte[data.Length];

        Assert.Throws<LZ4Exception>(() => LZ4.Decompress(Random(1000, 8), destination));

        // a frame followed by garbage
        var trailing = LZ4.Compress(data).Concat(Random(50, 9)).ToArray();
        Assert.Throws<LZ4Exception>(() => LZ4.Decompress(trailing, new byte[data.Length + 100]));

        // the error of lz4frame is reported as it is
        var compressed = LZ4.Compress(data, LZ4CompressionOptions.Default with { ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled });
        compressed[^1] ^= 0xFF;
        var ex = Assert.Throws<LZ4Exception>(() => LZ4.Decompress(compressed, destination));
        Assert.Contains("contentChecksum", ex.Message);
    }

    // The span overload and the array overload agree on every input: the same bytes, or both reject it.
    [Fact]
    public void MutatedInput_AgreesWithArrayOverload()
    {
        var data = Compressible(150_000, 10);
        var destination = new byte[4 * 1024 * 1024];
        var random = new Random(11);

        foreach (var options in OptionVariants)
        {
            var compressed = LZ4.Compress(data, options);
            for (var i = 0; i < 150; i++)
            {
                var mutated = (byte[])compressed.Clone();
                var position = i < 40 ? random.Next(0, Math.Min(32, mutated.Length)) : random.Next(0, mutated.Length);
                mutated[position] ^= (byte)(1 << random.Next(0, 8));
                if (i % 5 == 0) mutated = mutated.AsSpan(0, random.Next(1, mutated.Length)).ToArray();

                byte[]? expected = null;
                try { expected = LZ4.Decompress(mutated); } catch (LZ4Exception) { }

                byte[]? actual = null;
                try
                {
                    var written = LZ4.Decompress(mutated, destination);
                    actual = destination.AsSpan(0, written).ToArray();
                }
                catch (LZ4Exception ex) when (ex.Message != "Destination buffer is too small.")
                {
                }

                Assert.Equal(expected == null, actual == null);
                if (expected != null) Assert.True(expected.AsSpan().SequenceEqual(actual));
            }
        }
    }

    [Fact]
    public void AllocatesNothing()
    {
        var data = Compressible(300_000, 12);
        var compressed = LZ4.Compress(data).Concat(LZ4.Compress(data)).ToArray();
        var destination = new byte[data.Length * 2];

        for (var i = 0; i < 20; i++) LZ4.Decompress(compressed, destination);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++) LZ4.Decompress(compressed, destination);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---- dictionary

    // The native dictionary is freed by Dispose only when no lease on it is left, so this tells whether
    // the call gave its lease back. There is no public way to observe that.
    static bool IsNativeDictionaryFreed(LZ4Dictionary dictionary)
    {
        var handle = (SafeHandle)typeof(LZ4Dictionary).GetField("native", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dictionary)!;
        return handle.IsClosed;
    }

    [Fact]
    public void Dictionary_RoundTrip_AndLeaseIsReturned()
    {
        var dictionaryBytes = Compressible(64 * 1024, 13);
        var data = dictionaryBytes.AsSpan(1000, 30_000).ToArray().Concat(Compressible(100_000, 14)).ToArray();

        var dictionary = LZ4Dictionary.Create(dictionaryBytes, dictionaryId: 7);
        var compressed = LZ4.Compress(data, LZ4CompressionOptions.Default with { Dictionary = dictionary });
        var options = LZ4DecompressionOptions.Default with { Dictionary = dictionary };

        var destination = new byte[data.Length];
        Assert.Equal(data.Length, LZ4.Decompress(compressed, destination, options));
        Assert.Equal(data, destination);

        // without the dictionary the content differs or the frame is rejected
        var plain = new byte[data.Length];
        try
        {
            LZ4.Decompress(compressed, plain);
            Assert.NotEqual(data, plain);
        }
        catch (LZ4Exception)
        {
        }

        dictionary.Dispose();
        Assert.True(IsNativeDictionaryFreed(dictionary));
    }

    [Fact]
    public void Dictionary_LeaseIsReturnedWhenTheCallFails()
    {
        var dictionary = LZ4Dictionary.Create(Compressible(64 * 1024, 15));
        var data = Compressible(100_000, 16);
        var compressed = LZ4.Compress(data, LZ4CompressionOptions.Default with { Dictionary = dictionary });
        var options = LZ4DecompressionOptions.Default with { Dictionary = dictionary };

        Assert.Throws<LZ4Exception>(() => LZ4.Decompress(compressed, new byte[10], options)); // too small
        Assert.Throws<LZ4Exception>(() => LZ4.Decompress(compressed.AsSpan(0, compressed.Length / 2), new byte[data.Length], options)); // truncated
        Assert.Throws<LZ4Exception>(() => LZ4.Decompress(Random(500, 17), new byte[data.Length], options)); // invalid

        dictionary.Dispose();
        Assert.True(IsNativeDictionaryFreed(dictionary));
    }

    [Fact]
    public void Dictionary_Disposed_Throws()
    {
        var dictionary = LZ4Dictionary.Create(Compressible(1024, 18));
        var compressed = LZ4.Compress(Compressible(1000, 19));
        dictionary.Dispose();

        Assert.Throws<ObjectDisposedException>(() => LZ4.Decompress(compressed, new byte[1000], LZ4DecompressionOptions.Default with { Dictionary = dictionary }));
    }
}
