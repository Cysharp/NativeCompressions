using NativeCompressions.Lz4;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NativeCompressions.Tests;

public class LZ4Test
{
    [Fact]
    public void EncoderContinueCompress()
    {
        var src = new byte[1024];
        var totalWrite = 0;
        var span = src.AsSpan();
        using var encoder = new LZ4Encoder();

        //var written = encoder.Compress(EncodeUtf8("あいうえおあいうえおあいうえお"), span);
        //totalWrite += written;
        //span = span.Slice(written);

        //written = encoder.Compress(EncodeUtf8("あいうえおあいうえおあいうえお"), span);
        //totalWrite += written;
        //span = span.Slice(written);

        //written = encoder.Compress(EncodeUtf8("abcdefghijklmnopqrstu"), span);
        //totalWrite += written;
        //span = span.Slice(written);

        //var compressed = src.AsSpan(0, totalWrite).ToArray();

        //var dest = new byte[1024];

        //using var decoder = new LZ4Decoder();

        //written = decoder.Decompress(compressed, dest);


        //var str = Encoding.UTF8.GetString(dest.AsSpan(0, written));

        //str.Should().Be("あいうえおあいうえおあいうえおabcdefghijklmnopqrstu");
    }

    [Fact]
    public void HC()
    {
        var bin = LZ4Encoder.CompressHC(EncodeUtf8("あいうえおあいうえおあいうえお"), LZ4HCCompressionLevel.Default);

        byte[] dest = new byte[1024];
        var ok = LZ4Encoder.TryDecompress(bin, dest, out var bytesWritten);
        ok.Should().BeTrue();
        var str = Encoding.UTF8.GetString(dest.AsSpan(0, bytesWritten));
        str.Should().Be("あいうえおあいうえおあいうえお");
    }

    byte[] EncodeUtf8(string value)
    {
        return Encoding.UTF8.GetBytes(value);
    }
}
