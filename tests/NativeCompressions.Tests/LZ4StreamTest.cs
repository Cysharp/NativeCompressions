using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace NativeCompressions.Tests;

// Stream contract of LZ4Stream: properties, mode checks, byte APIs, APM, partial reads, multi frame, disposal.
public class LZ4StreamTest
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] Compressible(int size, int seed)
    {
        var rand = new Random(seed);
        var words = new[] { "lz4 ", "native ", "compression ", "dotnet ", "stream ", "\n" };
        var sb = new StringBuilder(size);
        while (sb.Length < size)
        {
            sb.Append(words[rand.Next(words.Length)]);
            if (rand.Next(50) == 0) sb.Append(rand.Next());
        }
        return Encoding.ASCII.GetBytes(sb.ToString(0, size));
    }

    static readonly byte[] Data = Compressible(3 * 1024 * 1024, seed: 91);

    static byte[] StreamCompress(byte[] data)
    {
        var ms = new MemoryStream();
        using (var zs = new LZ4Stream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            zs.Write(data);
        }
        return ms.ToArray();
    }

    sealed class TrickleStream(byte[] data, int maxRead) : Stream
    {
        int position;
        public int ReadCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            var n = Math.Min(Math.Min(count, maxRead), data.Length - position);
            Array.Copy(data, position, buffer, offset, n);
            position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Properties()
    {
        using var compress = new LZ4Stream(new MemoryStream(), CompressionMode.Compress);
        Assert.True(compress.CanWrite);
        Assert.False(compress.CanRead);
        Assert.False(compress.CanSeek);
        Assert.Throws<NotSupportedException>(() => compress.Length);
        Assert.Throws<NotSupportedException>(() => compress.Position);
        Assert.Throws<NotSupportedException>(() => compress.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => compress.SetLength(0));

        using var decompress = new LZ4Stream(new MemoryStream(LZ4.Compress(Data)), CompressionMode.Decompress);
        Assert.True(decompress.CanRead);
        Assert.False(decompress.CanWrite);
    }

    [Fact]
    public void WrongMode_Throws()
    {
        using var compress = new LZ4Stream(new MemoryStream(), CompressionMode.Compress);
        Assert.Throws<InvalidOperationException>(() => compress.Read(new byte[16], 0, 16));

        using var decompress = new LZ4Stream(new MemoryStream(LZ4.Compress(Data)), CompressionMode.Decompress);
        Assert.Throws<InvalidOperationException>(() => decompress.Write(new byte[16], 0, 16));
        Assert.Throws<InvalidOperationException>(() => decompress.Flush());
    }

    [Fact]
    public void WriteByte_ReadByte_RoundTrip()
    {
        var text = Utf8("byte by byte");
        var ms = new MemoryStream();
        using (var zs = new LZ4Stream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            foreach (var b in text) zs.WriteByte(b);
        }
        Assert.Equal(text, LZ4.Decompress(ms.ToArray()));

        using var reader = new LZ4Stream(new MemoryStream(ms.ToArray()), CompressionMode.Decompress);
        var result = new List<byte>();
        int value;
        while ((value = reader.ReadByte()) != -1) result.Add((byte)value);
        Assert.Equal(text, result.ToArray());
        Assert.Equal(-1, reader.ReadByte());
    }

    [Fact]
    public void Read_EmptyBuffer_ReturnsZero()
    {
        using var reader = new LZ4Stream(new MemoryStream(LZ4.Compress(Data)), CompressionMode.Decompress);
        Assert.Equal(0, reader.Read(Span<byte>.Empty));
        var ms = new MemoryStream();
        reader.CopyTo(ms);
        Assert.Equal(Data, ms.ToArray());
    }

    [Fact]
    public void Write_Nothing_ThenDispose_ProducesValidFrame()
    {
        var ms = new MemoryStream();
        using (var zs = new LZ4Stream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            zs.Write(ReadOnlySpan<byte>.Empty);
        }
        Assert.True(ms.Length > 0);
        Assert.Empty(LZ4.Decompress(ms.ToArray()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    [InlineData(65535)]
    public void Read_InnerStreamTrickles(int maxRead)
    {
        var compressed = LZ4.Compress(Data, LZ4CompressionOptions.Default with { ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled, BlockChecksumFlag = BlockChecksum.BlockChecksumEnabled });
        var inner = new TrickleStream(compressed, maxRead);

        using var reader = new LZ4Stream(inner, CompressionMode.Decompress);
        var ms = new MemoryStream();
        var buffer = new byte[4099];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, read);
        }
        Assert.Equal(Data, ms.ToArray());
        Assert.True(inner.ReadCalls >= compressed.Length / maxRead);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    public async Task ReadAsync_InnerStreamTrickles(int maxRead)
    {
        var compressed = LZ4.Compress(Data);
        await using var reader = new LZ4Stream(new TrickleStream(compressed, maxRead), CompressionMode.Decompress);
        var ms = new MemoryStream();
        var buffer = new byte[4099];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            ms.Write(buffer, 0, read);
        }
        Assert.Equal(Data, ms.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(13)]
    [InlineData(1000)]
    public void Read_SmallDestinationBuffers(int destinationSize)
    {
        var small = Data.AsSpan(0, 100 * 1024).ToArray();
        using var reader = new LZ4Stream(new MemoryStream(LZ4.Compress(small)), CompressionMode.Decompress);
        var ms = new MemoryStream();
        var buffer = new byte[destinationSize];
        int read;
        while ((read = reader.Read(buffer)) > 0)
        {
            ms.Write(buffer, 0, read);
        }
        Assert.Equal(small, ms.ToArray());
    }

    [Fact]
    public void Read_MultipleFrames_Concatenated()
    {
        var a = Data.AsSpan(0, 500_000).ToArray();
        var b = Data.AsSpan(500_000, 300_000).ToArray();
        var c = Utf8("tail");
        var empty = LZ4.Compress(ReadOnlySpan<byte>.Empty);
        var concatenated = LZ4.Compress(a).Concat(empty).Concat(StreamCompress(b)).Concat(LZ4.Compress(c, LZ4CompressionOptions.Default with { BlockMode = BlockMode.BlockIndependent })).ToArray();

        using var reader = new LZ4Stream(new TrickleStream(concatenated, 1234), CompressionMode.Decompress);
        var ms = new MemoryStream();
        reader.CopyTo(ms);
        Assert.Equal(a.Concat(b).Concat(c).ToArray(), ms.ToArray());
    }

    [Theory]
    [InlineData(BlockSizeId.Default)]
    [InlineData(BlockSizeId.Max4MB)]
    public void ManySmallWrites_ThenFlushAndClose(BlockSizeId blockSize)
    {
        // small writes are buffered inside LZ4F until a block is full, so Flush and Close
        // must have room for a whole block even though every Write was tiny
        var random = new byte[300 * 1024];
        new Random(5).NextBytes(random);

        var ms = new MemoryStream();
        using (var zs = new LZ4Stream(ms, LZ4CompressionOptions.Default with { BlockSizeID = blockSize }, leaveOpen: true))
        {
            for (int offset = 0; offset < random.Length; offset += 1000)
            {
                zs.Write(random, offset, Math.Min(1000, random.Length - offset));
            }
            zs.Flush();
            zs.Write(Utf8("tail"));
        }

        Assert.Equal(random.Concat(Utf8("tail")).ToArray(), LZ4.Decompress(ms.ToArray()));
    }

    [Fact]
    public void Flush_MakesDataDecodableBeforeClose()
    {
        var first = Utf8("first part written before flush ");
        var second = Utf8("second part after flush");

        var ms = new MemoryStream();
        using var zs = new LZ4Stream(ms, CompressionMode.Compress, leaveOpen: true);
        zs.Write(first);
        zs.Flush();

        using (var decoder = new LZ4Decoder())
        {
            var dest = new byte[1024];
            var status = decoder.Decompress(ms.ToArray(), dest, out _, out var written);
            Assert.Equal(OperationStatus.NeedMoreData, status);
            Assert.Equal(first, dest.AsSpan(0, written).ToArray());
        }

        zs.Write(second);
        zs.Dispose();
        Assert.Equal(first.Concat(second).ToArray(), LZ4.Decompress(ms.ToArray()));
    }

    [Fact]
    public async Task FlushAsync_MakesDataDecodableBeforeClose()
    {
        var first = Utf8("first part written before flush ");
        var ms = new MemoryStream();
        await using var zs = new LZ4Stream(ms, CompressionMode.Compress, leaveOpen: true);
        await zs.WriteAsync(first);
        await zs.FlushAsync();

        using var decoder = new LZ4Decoder();
        var dest = new byte[1024];
        Assert.Equal(OperationStatus.NeedMoreData, decoder.Decompress(ms.ToArray(), dest, out _, out var written));
        Assert.Equal(first, dest.AsSpan(0, written).ToArray());
    }

    [Fact]
    public void BeginWrite_EndWrite_And_BeginRead_EndRead()
    {
        var ms = new MemoryStream();
        using (var zs = new LZ4Stream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            zs.EndWrite(zs.BeginWrite(Data, 0, Data.Length, null, null));
        }
        Assert.Equal(Data, LZ4.Decompress(ms.ToArray()));

        using var reader = new LZ4Stream(new MemoryStream(ms.ToArray()), CompressionMode.Decompress);
        var result = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = reader.EndRead(reader.BeginRead(buffer, 0, buffer.Length, null, null));
            if (read == 0) break;
            result.Write(buffer, 0, read);
        }
        Assert.Equal(Data, result.ToArray());
    }

    [Fact]
    public async Task LargeData_AsyncWriteInPieces()
    {
        var ms = new MemoryStream();
        await using (var zs = new LZ4Stream(ms, LZ4CompressionOptions.Default with { CompressionLevel = 3 }, leaveOpen: true))
        {
            for (int offset = 0; offset < Data.Length; offset += 70_001)
            {
                await zs.WriteAsync(Data.AsMemory(offset, Math.Min(70_001, Data.Length - offset)));
            }
        }
        Assert.Equal(Data, LZ4.Decompress(ms.ToArray()));
    }

    [Fact]
    public async Task WriteAsync_CancelledToken()
    {
        await using var zs = new LZ4Stream(new MemoryStream(), CompressionMode.Compress);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await zs.WriteAsync(Data, cts.Token));
    }

    [Fact]
    public void Dispose_LeaveOpen()
    {
        var closed = new MemoryStream();
        var zs = new LZ4Stream(closed, CompressionMode.Compress, leaveOpen: false);
        zs.Write(Utf8("abc"));
        zs.Dispose();
        Assert.False(closed.CanWrite);

        var open = new MemoryStream();
        zs = new LZ4Stream(open, CompressionMode.Compress, leaveOpen: true);
        zs.Write(Utf8("abc"));
        zs.Dispose();
        Assert.True(open.CanWrite);
        Assert.Equal(Utf8("abc"), LZ4.Decompress(open.ToArray()));
    }

    [Fact]
    public void Dispose_Twice_And_UseAfterDispose()
    {
        var zs = new LZ4Stream(new MemoryStream(), CompressionMode.Compress);
        zs.Write(Utf8("abc"));
        zs.Dispose();
        zs.Dispose();
        Assert.Throws<ObjectDisposedException>(() => zs.Write(Utf8("x")));
        Assert.Throws<ObjectDisposedException>(() => zs.Flush());

        var reader = new LZ4Stream(new MemoryStream(LZ4.Compress(Data)), CompressionMode.Decompress);
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.Read(new byte[16], 0, 16));
    }

    [Fact]
    public async Task DisposeAsync_FinishesFrame()
    {
        var ms = new MemoryStream();
        var zs = new LZ4Stream(ms, CompressionMode.Compress, leaveOpen: true);
        await zs.WriteAsync(Data);
        await zs.DisposeAsync();
        await zs.DisposeAsync();
        Assert.Equal(Data, LZ4.Decompress(ms.ToArray()));
    }

    [Fact]
    public void ExternalEncoderAndDecoder_SurviveStreamDispose()
    {
        using var encoder = new LZ4Encoder(LZ4CompressionOptions.Default with { CompressionLevel = 4 });
        var ms = new MemoryStream();
        using (var zs = new LZ4Stream(ms, encoder, leaveOpen: true))
        {
            zs.Write(Data);
        }
        Assert.False(encoder.IsDisposed);
        Assert.Equal(Data, LZ4.Decompress(ms.ToArray()));

        using var decoder = new LZ4Decoder();
        using (var reader = new LZ4Stream(new MemoryStream(ms.ToArray()), decoder, leaveOpen: true))
        {
            reader.CopyTo(Stream.Null);
        }
        Assert.False(decoder.IsDisposed);
    }

    [Fact]
    public void ExternalDecoder_StartedFrame_EmptyRemainderThrows()
    {
        var compressed = LZ4.Compress(Data);
        using var decoder = new LZ4Decoder();
        _ = decoder.GetFrameInfo(compressed, out _);

        using var reader = new LZ4Stream(new MemoryStream(), decoder, leaveOpen: true);
        Assert.Throws<InvalidOperationException>(() => reader.CopyTo(Stream.Null));
        Assert.False(decoder.IsDisposed);
    }

    [Fact]
    public async Task ExternalDecoder_StartedFrame_EmptyRemainderThrowsAsync()
    {
        var compressed = LZ4.Compress(Data);
        using var decoder = new LZ4Decoder();
        _ = decoder.GetFrameInfo(compressed, out _);

        await using var reader = new LZ4Stream(new MemoryStream(), decoder, leaveOpen: true);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken));
        Assert.False(decoder.IsDisposed);
    }

    [Fact]
    public void ExternalDecoder_FreshOrCompletedFrame_EmptyInputIsCleanEof()
    {
        using var freshDecoder = new LZ4Decoder();
        using (var freshReader = new LZ4Stream(new MemoryStream(), freshDecoder, leaveOpen: true))
        {
            Assert.Equal(0, freshReader.Read(new byte[1]));
        }

        var compressed = LZ4.Compress(Data);
        using var completedDecoder = new LZ4Decoder();
        var destination = new byte[Data.Length];
        Assert.Equal(
            OperationStatus.Done,
            completedDecoder.Decompress(compressed, destination, out _, out _));

        using var completedReader = new LZ4Stream(new MemoryStream(), completedDecoder, leaveOpen: true);
        Assert.Equal(0, completedReader.Read(new byte[1]));
    }

    [Fact]
    public void Read_TruncatedInput_Throws()
    {
        var compressed = LZ4.Compress(Data);

        foreach (var truncated in new[]
        {
            compressed.AsSpan(0, compressed.Length / 2).ToArray(),
            compressed.AsSpan(0, compressed.Length - 1).ToArray(),
        })
        {
            using var reader = new LZ4Stream(new MemoryStream(truncated), CompressionMode.Decompress);
            Assert.Throws<InvalidOperationException>(() => reader.CopyTo(Stream.Null));
        }
    }

    [Fact]
    public async Task ReadAsync_TruncatedInput_Throws()
    {
        var compressed = LZ4.Compress(Data, LZ4CompressionOptions.Default with
        {
            ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled,
        });

        foreach (var truncated in new[]
        {
            compressed.AsSpan(0, compressed.Length / 2).ToArray(),
            compressed.AsSpan(0, compressed.Length - 1).ToArray(),
        })
        {
            await using var reader = new LZ4Stream(new MemoryStream(truncated), CompressionMode.Decompress);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => reader.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void Read_CompleteFrameFollowedByTruncatedFrame_Throws()
    {
        var complete = LZ4.Compress(Data);
        var next = LZ4.Compress(Utf8("next frame"));
        var input = complete.Concat(next.Take(next.Length / 2)).ToArray();

        using var reader = new LZ4Stream(new MemoryStream(input), CompressionMode.Decompress);
        Assert.Throws<InvalidOperationException>(() => reader.CopyTo(Stream.Null));
    }

    [Fact]
    public async Task ReadAsync_CompleteFrameFollowedByTruncatedFrame_Throws()
    {
        var complete = LZ4.Compress(Data);
        var next = LZ4.Compress(Utf8("next frame"));
        var input = complete.Concat(next.Take(next.Length / 2)).ToArray();

        await using var reader = new LZ4Stream(new MemoryStream(input), CompressionMode.Decompress);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Read_CorruptInput_Throws()
    {
        var garbage = new byte[4096];
        new Random(1).NextBytes(garbage);
        using var reader = new LZ4Stream(new MemoryStream(garbage), CompressionMode.Decompress);
        Assert.Throws<InvalidOperationException>(() => reader.CopyTo(Stream.Null));

        var compressed = LZ4.Compress(Data, LZ4CompressionOptions.Default with { BlockChecksumFlag = BlockChecksum.BlockChecksumEnabled });
        compressed[compressed.Length / 2] ^= 0xFF;
        using var reader2 = new LZ4Stream(new MemoryStream(compressed), CompressionMode.Decompress);
        Assert.Throws<InvalidOperationException>(() => reader2.CopyTo(Stream.Null));
    }
}
