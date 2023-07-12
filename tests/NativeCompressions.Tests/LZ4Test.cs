using NativeCompressions.Lz4;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NativeCompressions.Tests;

public class LZ4Test
{
    [Fact(Skip ="WIP")]
    public void EncoderContinueCompress()
    {
        var src = new byte[1024];
        var totalWrite = 0;
        var span = src.AsSpan();
        using var encoder = new LZ4Encoder();

        var written = encoder.Compress(EncodeUtf8("あいうえおあいうえおあいうえお"), span);
        totalWrite += written;
        span = span.Slice(written);

        written = encoder.Compress(EncodeUtf8("abcdefghijklmnopqrstu"), span);
        totalWrite += written;
        span = span.Slice(written);

        var compressed = src.AsSpan(0, totalWrite).ToArray();

        var dest = new byte[1024];
        var ok = LZ4Encoder.TryDecompress(compressed, dest, out written);
        ok.Should().BeTrue();
        var str = Encoding.UTF8.GetString(dest.AsSpan(0, written));

        str.Should().Be("あいうえおあいうえおあいうえおabcdefghijklmnopqrstu");
    }

    byte[] EncodeUtf8(string value)
    {
        return Encoding.UTF8.GetBytes(value);
    }
}
