using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace NativeCompressions.Tests;

// ZstandardStream must reject input that ends inside a frame. Compiled for every target framework.
public class ZstandardStreamTruncationTest
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static readonly byte[] Data = CreateData();

    static byte[] CreateData()
    {
        var rand = new Random(17);
        var data = new byte[512 * 1024];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)('a' + rand.Next(8));
        }
        return data;
    }

    static byte[][] Truncations(byte[] compressed) =>
    [
        compressed.AsSpan(0, 1).ToArray(),
        compressed.AsSpan(0, compressed.Length / 2).ToArray(),
        compressed.AsSpan(0, compressed.Length - 1).ToArray(),
    ];

    [Fact]
    public void Read_TruncatedInput_Throws()
    {
        foreach (var truncated in Truncations(Zstandard.Compress(Data)))
        {
            using var reader = new ZstandardStream(new MemoryStream(truncated), CompressionMode.Decompress);
            Assert.Throws<ZstandardException>(() => reader.CopyTo(Stream.Null));
        }
    }

    [Fact]
    public async Task ReadAsync_TruncatedInput_Throws()
    {
        foreach (var truncated in Truncations(Zstandard.Compress(Data)))
        {
            using var reader = new ZstandardStream(new MemoryStream(truncated), CompressionMode.Decompress);
            await Assert.ThrowsAsync<ZstandardException>(
                () => reader.CopyToAsync(Stream.Null, 81920, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void Read_CompleteFrameFollowedByTruncatedFrame_Throws()
    {
        var complete = Zstandard.Compress(Data);
        var next = Zstandard.Compress(Utf8("next frame"));
        var input = complete.Concat(next.Take(next.Length / 2)).ToArray();

        using var reader = new ZstandardStream(new MemoryStream(input), CompressionMode.Decompress);
        Assert.Throws<ZstandardException>(() => reader.CopyTo(Stream.Null));
    }

    [Fact]
    public async Task ReadAsync_CompleteFrameFollowedByTruncatedFrame_Throws()
    {
        var complete = Zstandard.Compress(Data);
        var next = Zstandard.Compress(Utf8("next frame"));
        var input = complete.Concat(next.Take(next.Length / 2)).ToArray();

        using var reader = new ZstandardStream(new MemoryStream(input), CompressionMode.Decompress);
        await Assert.ThrowsAsync<ZstandardException>(
            () => reader.CopyToAsync(Stream.Null, 81920, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Read_CompleteFrames_ReachCleanEof()
    {
        var first = Zstandard.Compress(Data);
        var second = Zstandard.Compress(Utf8("next frame"));
        var input = first.Concat(second).ToArray();

        using var reader = new ZstandardStream(new MemoryStream(input), CompressionMode.Decompress);
        var ms = new MemoryStream();
        reader.CopyTo(ms);

        Assert.Equal(Data.Concat(Utf8("next frame")).ToArray(), ms.ToArray());
        Assert.Equal(0, reader.Read(new byte[1], 0, 1));
    }

    [Fact]
    public void Read_EmptyInput_IsCleanEof()
    {
        using var reader = new ZstandardStream(new MemoryStream(), CompressionMode.Decompress);
        Assert.Equal(0, reader.Read(new byte[1], 0, 1));
        Assert.Equal(0, reader.Read(new byte[1], 0, 1));
    }

    [Fact]
    public void Read_CorruptInput_Throws()
    {
        var garbage = new byte[4096];
        new Random(1).NextBytes(garbage);
        using var reader = new ZstandardStream(new MemoryStream(garbage), CompressionMode.Decompress);
        Assert.Throws<ZstandardException>(() => reader.CopyTo(Stream.Null));
    }

    [Fact]
    public void ExternalDecoder_StartedFrame_EmptyRemainderThrows()
    {
        var compressed = Zstandard.Compress(Data);
        using var decoder = new ZstandardDecoder();
        var status = decoder.Decompress(compressed.AsSpan(0, compressed.Length / 2), new byte[1024], out _, out _);
        Assert.NotEqual(OperationStatus.Done, status);
        Assert.NotEqual(OperationStatus.InvalidData, status);

        using var reader = new ZstandardStream(new MemoryStream(), decoder, leaveOpen: true);
        Assert.Throws<ZstandardException>(() => reader.CopyTo(Stream.Null));
        Assert.False(decoder.IsDisposed);
    }

    [Fact]
    public void ExternalDecoder_FreshCompletedOrReset_EmptyInputIsCleanEof()
    {
        using var fresh = new ZstandardDecoder();
        using (var reader = new ZstandardStream(new MemoryStream(), fresh, leaveOpen: true))
        {
            Assert.Equal(0, reader.Read(new byte[1], 0, 1));
        }

        var compressed = Zstandard.Compress(Data);
        using var completed = new ZstandardDecoder();
        Assert.Equal(OperationStatus.Done, completed.Decompress(compressed, new byte[Data.Length], out _, out _));
        using (var reader = new ZstandardStream(new MemoryStream(), completed, leaveOpen: true))
        {
            Assert.Equal(0, reader.Read(new byte[1], 0, 1));
        }

        using var reset = new ZstandardDecoder();
        reset.Decompress(compressed.AsSpan(0, compressed.Length / 2), new byte[1024], out _, out _);
        reset.Reset();
        using (var reader = new ZstandardStream(new MemoryStream(), reset, leaveOpen: true))
        {
            Assert.Equal(0, reader.Read(new byte[1], 0, 1));
        }
    }
}
