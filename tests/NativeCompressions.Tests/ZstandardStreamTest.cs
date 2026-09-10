using System.Buffers;
using System.Text;
using BclDecoder = System.IO.Compression.ZstandardDecoder;
using BclStream = System.IO.Compression.ZstandardStream;
using CompressionMode = System.IO.Compression.CompressionMode;

namespace NativeCompressions.Tests;

// Stream contract of ZstandardStream: properties, mode checks, byte APIs, APM, partial reads, multi frame, disposal.
public class ZstandardStreamTest
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] Compressible(int size, int seed)
    {
        var rand = new Random(seed);
        var words = new[] { "zstd ", "native ", "compression ", "dotnet ", "stream ", "\n" };
        var sb = new StringBuilder(size);
        while (sb.Length < size)
        {
            sb.Append(words[rand.Next(words.Length)]);
            if (rand.Next(50) == 0) sb.Append(rand.Next());
        }
        return Encoding.ASCII.GetBytes(sb.ToString(0, size));
    }

    static readonly byte[] Data = Compressible(3 * 1024 * 1024, seed: 31);

    static byte[] BclDecompress(byte[] compressed)
    {
        using var zs = new BclStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var ms = new MemoryStream();
        zs.CopyTo(ms);
        return ms.ToArray();
    }

    static byte[] BclCompress(byte[] data)
    {
        var ms = new MemoryStream();
        using (var zs = new BclStream(ms, new System.IO.Compression.ZstandardCompressionOptions { Quality = 3 }, leaveOpen: true))
        {
            zs.Write(data);
        }
        return ms.ToArray();
    }

    // Inner stream that returns at most maxRead bytes per call, to exercise partial reads.
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

    // ---- properties and unsupported members

    [Fact]
    public void Properties_CompressMode()
    {
        using var zs = new ZstandardStream(new MemoryStream(), CompressionMode.Compress);
        Assert.True(zs.CanWrite);
        Assert.False(zs.CanRead);
        Assert.False(zs.CanSeek);
        Assert.Throws<NotSupportedException>(() => zs.Length);
        Assert.Throws<NotSupportedException>(() => zs.Position);
        Assert.Throws<NotSupportedException>(() => zs.Position = 0);
        Assert.Throws<NotSupportedException>(() => zs.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => zs.SetLength(0));
    }

    [Fact]
    public void Properties_DecompressMode()
    {
        using var zs = new ZstandardStream(new MemoryStream(Zstandard.Compress(Data)), CompressionMode.Decompress);
        Assert.True(zs.CanRead);
        Assert.False(zs.CanWrite);
        Assert.False(zs.CanSeek);
    }

    [Fact]
    public void Properties_FollowInnerStream()
    {
        // a write only inner stream cannot be read even in decompress mode
        var writeOnly = new MemoryStream(new byte[16], writable: true);
        using var compress = new ZstandardStream(writeOnly, CompressionMode.Compress);
        Assert.True(compress.CanWrite);

        var readOnly = new MemoryStream(Zstandard.Compress(Data), writable: false);
        using var decompress = new ZstandardStream(readOnly, CompressionMode.Decompress);
        Assert.True(decompress.CanRead);
    }

    // ---- mode mismatch

    [Fact]
    public void WrongMode_Throws()
    {
        using var compress = new ZstandardStream(new MemoryStream(), CompressionMode.Compress);
        Assert.Throws<InvalidOperationException>(() => compress.Read(new byte[16], 0, 16));
        Assert.Throws<InvalidOperationException>(() => compress.ReadByte());

        using var decompress = new ZstandardStream(new MemoryStream(Zstandard.Compress(Data)), CompressionMode.Decompress);
        Assert.Throws<InvalidOperationException>(() => decompress.Write(new byte[16], 0, 16));
        Assert.Throws<InvalidOperationException>(() => decompress.WriteByte(1));
        Assert.Throws<InvalidOperationException>(() => decompress.Flush());
    }

    // ---- byte oriented APIs

    [Fact]
    public void WriteByte_ReadByte_RoundTrip()
    {
        var text = Utf8("byte by byte");

        var ms = new MemoryStream();
        using (var zs = new ZstandardStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            foreach (var b in text) zs.WriteByte(b);
        }
        Assert.Equal(text, BclDecompress(ms.ToArray()));

        using var reader = new ZstandardStream(new MemoryStream(ms.ToArray()), CompressionMode.Decompress);
        var result = new List<byte>();
        int value;
        while ((value = reader.ReadByte()) != -1) result.Add((byte)value);
        Assert.Equal(text, result.ToArray());
        Assert.Equal(-1, reader.ReadByte()); // stays at EOF
    }

    [Fact]
    public void Read_EmptyBuffer_ReturnsZero()
    {
        using var reader = new ZstandardStream(new MemoryStream(Zstandard.Compress(Data)), CompressionMode.Decompress);
        Assert.Equal(0, reader.Read(Span<byte>.Empty));
        Assert.Equal(0, reader.Read(new byte[0], 0, 0));

        // and the data is still all there afterwards
        var ms = new MemoryStream();
        reader.CopyTo(ms);
        Assert.Equal(Data, ms.ToArray());
    }

    [Fact]
    public void Write_EmptyBuffer_ThenDispose_ProducesValidFrame()
    {
        var ms = new MemoryStream();
        using (var zs = new ZstandardStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            zs.Write(ReadOnlySpan<byte>.Empty);
        }

        Assert.True(ms.Length > 0);
        Assert.Empty(BclDecompress(ms.ToArray()));
    }

    // ---- partial reads from the inner stream

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    [InlineData(65535)]
    public void Read_InnerStreamTrickles(int maxRead)
    {
        var compressed = BclCompress(Data);
        var inner = new TrickleStream(compressed, maxRead);

        using var reader = new ZstandardStream(inner, CompressionMode.Decompress);
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
        var compressed = BclCompress(Data);
        await using var reader = new ZstandardStream(new TrickleStream(compressed, maxRead), CompressionMode.Decompress);

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
        using var reader = new ZstandardStream(new MemoryStream(BclCompress(small)), CompressionMode.Decompress);

        var ms = new MemoryStream();
        var buffer = new byte[destinationSize];
        int read;
        while ((read = reader.Read(buffer)) > 0)
        {
            ms.Write(buffer, 0, read);
        }
        Assert.Equal(small, ms.ToArray());
    }

    // ---- multiple frames

    [Fact]
    public void Read_MultipleFrames_Concatenated()
    {
        var a = Data.AsSpan(0, 500_000).ToArray();
        var b = Data.AsSpan(500_000, 300_000).ToArray();
        var c = Utf8("tail");
        var concatenated = BclCompress(a).Concat(Zstandard.Compress(b)).Concat(BclCompress(c)).ToArray();

        using var reader = new ZstandardStream(new TrickleStream(concatenated, 1234), CompressionMode.Decompress);
        var ms = new MemoryStream();
        reader.CopyTo(ms);
        Assert.Equal(a.Concat(b).Concat(c).ToArray(), ms.ToArray());
    }

    [Fact]
    public void Read_MultipleFrames_WithEmptyFrames()
    {
        var empty = Zstandard.Compress(ReadOnlySpan<byte>.Empty);
        var body = Zstandard.Compress(Utf8("body"));
        var concatenated = empty.Concat(body).Concat(empty).Concat(empty).ToArray();

        using var reader = new ZstandardStream(new MemoryStream(concatenated), CompressionMode.Decompress);
        var ms = new MemoryStream();
        reader.CopyTo(ms);
        Assert.Equal(Utf8("body"), ms.ToArray());
    }

    // ---- flush

    [Fact]
    public void Flush_MakesDataDecodableBeforeClose()
    {
        var first = Utf8("first part written before flush ");
        var second = Utf8("second part after flush");

        var ms = new MemoryStream();
        using var zs = new ZstandardStream(ms, CompressionMode.Compress, leaveOpen: true);
        zs.Write(first);
        zs.Flush();

        using (var decoder = new BclDecoder())
        {
            var dest = new byte[1024];
            var status = decoder.Decompress(ms.ToArray(), dest, out _, out var written);
            Assert.Equal(OperationStatus.NeedMoreData, status);
            Assert.Equal(first, dest.AsSpan(0, written).ToArray());
        }

        zs.Write(second);
        zs.Dispose();
        Assert.Equal(first.Concat(second).ToArray(), BclDecompress(ms.ToArray()));
    }

    [Fact]
    public void Flush_LargeIncompressibleData_IsFullyWrittenOut()
    {
        // pending output larger than the stream's 64KB buffer forces Flush to loop on DestinationTooSmall
        var random = new byte[300 * 1024];
        new Random(77).NextBytes(random);

        var ms = new MemoryStream();
        using var zs = new ZstandardStream(ms, CompressionMode.Compress, leaveOpen: true);
        zs.Write(random);
        zs.Flush();

        // leave room so a completely filled destination is not reported as DestinationTooSmall
        using var decoder = new BclDecoder();
        var dest = new byte[random.Length + 1024];
        var status = decoder.Decompress(ms.ToArray(), dest, out _, out var written);
        Assert.Equal(OperationStatus.NeedMoreData, status);
        Assert.Equal(random.Length, written);
        Assert.Equal(random, dest.AsSpan(0, written).ToArray());
    }

    [Fact]
    public void Flush_BeforeAnyWrite_IsNoOp()
    {
        var ms = new MemoryStream();
        using var zs = new ZstandardStream(ms, CompressionMode.Compress, leaveOpen: true);
        zs.Flush();
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public async Task FlushAsync_MakesDataDecodableBeforeClose()
    {
        var first = Utf8("first part written before flush ");
        var ms = new MemoryStream();
        await using var zs = new ZstandardStream(ms, CompressionMode.Compress, leaveOpen: true);
        await zs.WriteAsync(first);
        await zs.FlushAsync();

        using var decoder = new BclDecoder();
        var dest = new byte[1024];
        Assert.Equal(OperationStatus.NeedMoreData, decoder.Decompress(ms.ToArray(), dest, out _, out var written));
        Assert.Equal(first, dest.AsSpan(0, written).ToArray());
    }

    // ---- APM

    [Fact]
    public void BeginWrite_EndWrite_And_BeginRead_EndRead()
    {
        var ms = new MemoryStream();
        using (var zs = new ZstandardStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            var ar = zs.BeginWrite(Data, 0, Data.Length, null, null);
            zs.EndWrite(ar);
        }
        Assert.Equal(Data, BclDecompress(ms.ToArray()));

        using var reader = new ZstandardStream(new MemoryStream(ms.ToArray()), CompressionMode.Decompress);
        var result = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var ar = reader.BeginRead(buffer, 0, buffer.Length, null, null);
            var read = reader.EndRead(ar);
            if (read == 0) break;
            result.Write(buffer, 0, read);
        }
        Assert.Equal(Data, result.ToArray());
    }

    // ---- large data across the internal 64KB buffer

    [Fact]
    public async Task LargeData_AsyncWriteInPieces_ThenBclReads()
    {
        var ms = new MemoryStream();
        await using (var zs = new ZstandardStream(ms, ZstandardCompressionOptions.Default with { CompressionLevel = 1 }, leaveOpen: true))
        {
            for (int offset = 0; offset < Data.Length; offset += 70_001)
            {
                var n = Math.Min(70_001, Data.Length - offset);
                await zs.WriteAsync(Data.AsMemory(offset, n));
            }
        }
        Assert.Equal(Data, BclDecompress(ms.ToArray()));
    }

    [Fact]
    public async Task WriteAsync_CancelledToken()
    {
        await using var zs = new ZstandardStream(new MemoryStream(), CompressionMode.Compress);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await zs.WriteAsync(Data, cts.Token));
    }

    // ---- disposal

    [Fact]
    public void Dispose_LeaveOpenFalse_ClosesInner()
    {
        var inner = new MemoryStream();
        var zs = new ZstandardStream(inner, CompressionMode.Compress, leaveOpen: false);
        zs.Write(Utf8("abc"));
        zs.Dispose();
        Assert.False(inner.CanWrite);
    }

    [Fact]
    public void Dispose_LeaveOpenTrue_KeepsInner()
    {
        var inner = new MemoryStream();
        var zs = new ZstandardStream(inner, CompressionMode.Compress, leaveOpen: true);
        zs.Write(Utf8("abc"));
        zs.Dispose();
        Assert.True(inner.CanWrite);
        Assert.Equal(Utf8("abc"), BclDecompress(inner.ToArray()));
    }

    [Fact]
    public void Dispose_Twice_And_UseAfterDispose()
    {
        var zs = new ZstandardStream(new MemoryStream(), CompressionMode.Compress);
        zs.Write(Utf8("abc"));
        zs.Dispose();
        zs.Dispose();

        Assert.Throws<ObjectDisposedException>(() => zs.Write(Utf8("x")));
        Assert.Throws<ObjectDisposedException>(() => zs.WriteByte(1));
        Assert.Throws<ObjectDisposedException>(() => zs.Flush());
        Assert.Throws<ObjectDisposedException>(() => zs.BaseStream);

        var reader = new ZstandardStream(new MemoryStream(Zstandard.Compress(Data)), CompressionMode.Decompress);
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.Read(new byte[16], 0, 16));
        Assert.Throws<ObjectDisposedException>(() => reader.ReadByte());
    }

    [Fact]
    public async Task DisposeAsync_FinishesFrame()
    {
        var ms = new MemoryStream();
        var zs = new ZstandardStream(ms, CompressionMode.Compress, leaveOpen: true);
        await zs.WriteAsync(Data);
        await zs.DisposeAsync();
        await zs.DisposeAsync();
        Assert.Equal(Data, BclDecompress(ms.ToArray()));
    }

    [Fact]
    public void ExternalDecoder_SurvivesStreamDispose()
    {
        using var decoder = new ZstandardDecoder();
        var compressed = Zstandard.Compress(Data);

        using (var reader = new ZstandardStream(new MemoryStream(compressed), CompressionMode.Decompress, leaveOpen: true))
        {
            reader.CopyTo(Stream.Null);
        }
        Assert.False(decoder.IsDisposed);

        // the decoder can be used directly afterwards
        decoder.Reset();
        var dest = new byte[Data.Length];
        Assert.Equal(OperationStatus.Done, decoder.Decompress(compressed, dest, out _, out var written));
        Assert.Equal(Data, dest.AsSpan(0, written).ToArray());
    }

    [Fact]
    public void ExternalEncoder_Stream_WithOptionsCtor()
    {
        using var encoder = new ZstandardEncoder(ZstandardCompressionOptions.Default with { CompressionLevel = 6, ChecksumFlag = true });
        var ms = new MemoryStream();
        using (var zs = new ZstandardStream(ms, encoder, leaveOpen: true))
        {
            zs.Write(Data);
        }
        Assert.False(encoder.IsDisposed);
        Assert.Equal(Data, BclDecompress(ms.ToArray()));
    }

    // ---- truncated and corrupt input

    [Fact]
    public void Read_TruncatedInput_StopsAtAvailableData()
    {
        var compressed = BclCompress(Data);
        var truncated = compressed.AsSpan(0, compressed.Length / 2).ToArray();

        using var reader = new ZstandardStream(new MemoryStream(truncated), CompressionMode.Decompress);
        var ms = new MemoryStream();
        reader.CopyTo(ms);

        // whatever came out must be a prefix of the original
        Assert.True(ms.Length < Data.Length);
        Assert.Equal(Data.AsSpan(0, (int)ms.Length).ToArray(), ms.ToArray());
    }

    [Fact]
    public void Read_CorruptInput_Throws()
    {
        var garbage = new byte[4096];
        new Random(1).NextBytes(garbage);
        using var reader = new ZstandardStream(new MemoryStream(garbage), CompressionMode.Decompress);
        Assert.ThrowsAny<Exception>(() => reader.CopyTo(Stream.Null));
    }
}
