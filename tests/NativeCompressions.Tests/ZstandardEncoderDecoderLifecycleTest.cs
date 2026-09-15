using System.Buffers;
using System.Text;

namespace NativeCompressions.Tests;

// ZstandardEncoder / ZstandardDecoder are sealed classes that own a raw native context.
// These tests pin down the ownership semantics of that design.
public class ZstandardEncoderDecoderLifecycleTest
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] CompressAll(ZstandardEncoder encoder, ReadOnlySpan<byte> source)
    {
        var dest = new byte[Zstandard.GetMaxCompressedLength(source.Length)];
        var status = encoder.Compress(source, dest, out var consumed, out var written, isFinalBlock: true);
        if (status != OperationStatus.Done) throw new InvalidOperationException(status.ToString());
        if (consumed != source.Length) throw new InvalidOperationException("not fully consumed");
        return dest.AsSpan(0, written).ToArray();
    }

    static byte[] DecompressAll(ZstandardDecoder decoder, ReadOnlySpan<byte> source, int expectedLength)
    {
        var dest = new byte[expectedLength];
        var status = decoder.Decompress(source, dest, out var consumed, out var written);
        if (status != OperationStatus.Done) throw new InvalidOperationException(status.ToString());
        return dest.AsSpan(0, written).ToArray();
    }

    [Fact]
    public void RoundTrip()
    {
        var text = "あいうえおあいうえおあいうえおかきくけこ";
        var bin = Utf8(text);

        using var encoder = new ZstandardEncoder();
        using var decoder = new ZstandardDecoder();

        var compressed = CompressAll(encoder, bin);
        var decompressed = DecompressAll(decoder, compressed, bin.Length);

        Assert.Equal(text, Encoding.UTF8.GetString(decompressed));
    }

    [Fact]
    public void SharedReferenceSemantics()
    {
        // class: a second reference is the same object, not a copy.
        var encoder = new ZstandardEncoder();
        var alias = encoder;

        Assert.Same(encoder, alias);

        alias.Dispose();
        Assert.True(encoder.IsDisposed);
    }

    [Fact]
    public void DoubleDisposeIsSafe()
    {
        var encoder = new ZstandardEncoder();
        var decoder = new ZstandardDecoder();

        encoder.Dispose();
        encoder.Dispose();
        decoder.Dispose();
        decoder.Dispose();

        Assert.True(encoder.IsDisposed);
        Assert.True(decoder.IsDisposed);
    }

    [Fact]
    public void UseAfterDisposeThrowsObjectDisposedException()
    {
        var encoder = new ZstandardEncoder();
        var decoder = new ZstandardDecoder();
        encoder.Dispose();
        decoder.Dispose();

        var dest = new byte[128];

        Assert.Throws<ObjectDisposedException>(() => encoder.Compress(Utf8("abc"), dest, out _, out _, isFinalBlock: true));
        Assert.Throws<ObjectDisposedException>(() => encoder.Flush(dest, out _));
        Assert.Throws<ObjectDisposedException>(() => encoder.Close(dest, out _));
        Assert.Throws<ObjectDisposedException>(() => encoder.Reset());
        Assert.Throws<ObjectDisposedException>(() => encoder.Reset(ZstandardCompressionOptions.Default));

        Assert.Throws<ObjectDisposedException>(() => decoder.Decompress(Utf8("abc"), dest, out _, out _));
        Assert.Throws<ObjectDisposedException>(() => decoder.Reset());
        Assert.Throws<ObjectDisposedException>(() => decoder.Reset(ZstandardDecompressionOptions.Default));
    }

    [Fact]
    public void ResetAllowsNewFrameWithSameEncoder()
    {
        var a = Utf8("first frame first frame first frame");
        var b = Utf8("second frame second frame");

        using var encoder = new ZstandardEncoder(3);
        using var decoder = new ZstandardDecoder();

        var ca = CompressAll(encoder, a);
        encoder.Reset();
        var cb = CompressAll(encoder, b);

        var da = DecompressAll(decoder, ca, a.Length);
        decoder.Reset();
        var db = DecompressAll(decoder, cb, b.Length);

        Assert.Equal(a, da);
        Assert.Equal(b, db);
    }

    [Fact]
    public void InvalidParameterInConstructorDoesNotLeakOrCrash()
    {
        // ZSTD clamps compressionLevel, but windowLog is bounds-checked (min 10) and fails with parameter_outOfBound.
        var options = ZstandardCompressionOptions.Default with { WindowLog = 5 };

        Assert.Throws<ZstandardException>(() => new ZstandardEncoder(options));

        // The partially constructed object was disposed in the ctor; its finalizer must be a no-op.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Fact]
    public void FinalizerReleasesUndisposedContext()
    {
        CreateAndDrop();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // no crash / double free means the finalizer path is sound

        static void CreateAndDrop()
        {
            var encoder = new ZstandardEncoder();
            var decoder = new ZstandardDecoder();
            var dest = new byte[256];
            encoder.Compress(Utf8("abc"), dest, out _, out _, isFinalBlock: true);
            decoder.Decompress(dest, new byte[16], out _, out _);
            // intentionally not disposed
        }
    }

    [Fact]
    public void StreamWithExternalEncoderDoesNotDisposeIt()
    {
        var text = "stream stream stream stream stream";
        var bin = Utf8(text);

        using var encoder = new ZstandardEncoder();
        var ms = new MemoryStream();

        using (var zs = new ZstandardStream(ms, encoder, leaveOpen: true))
        {
            zs.Write(bin, 0, bin.Length);
        }

        // ZstandardStream was given the encoder by the caller, so it must not dispose it.
        Assert.False(encoder.IsDisposed);

        var decompressed = Zstandard.Decompress(ms.ToArray());
        Assert.Equal(text, Encoding.UTF8.GetString(decompressed));

        // encoder is still usable for another frame
        encoder.Reset();
        var again = CompressAll(encoder, bin);
        Assert.Equal(text, Encoding.UTF8.GetString(Zstandard.Decompress(again)));
    }

    [Fact]
    public void StreamWithOwnEncoderDisposesIt()
    {
        var ms = new MemoryStream();
        var zs = new ZstandardStream(ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true);
        zs.Write(Utf8("abc"), 0, 3);
        zs.Dispose();

        // must not throw: encoder/decoder fields are nullable and disposed with ?.
        zs.Dispose();
        Assert.True(ms.Length > 0);
    }

    [Fact]
    public void StreamConstructorRejectsNullEncoderAndDecoder()
    {
        var ms = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() => new ZstandardStream(ms, (ZstandardEncoder)null!));
        Assert.Throws<ArgumentNullException>(() => new ZstandardStream(ms, (ZstandardDecoder)null!));
    }
}
