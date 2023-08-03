using NativeCompressions.ZStandard;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace NativeCompressions.Tests;

public class ZStdTests
{
    [Fact]
    public void SimpleCompress()
    {
        var text = "あいうえおあいうえおあいうえおかきくけこ";
        var bin = EncodeUtf8(text);
        var dest = new byte[1024];
        ZStdEncoder.TryCompress(bin, dest, out var written).Should().BeTrue();

        var more = new byte[1024];
        ZStdEncoder.TryDecompress(dest.AsSpan(0, written), more, out var written2).Should().BeTrue();

        DecodeUtf8(more.AsSpan(0, written2)).Should().Be(text);
    }

    [DebuggerStepThrough]
    byte[] EncodeUtf8(string value)
    {
        return Encoding.UTF8.GetBytes(value);
    }

    [DebuggerStepThrough]
    string DecodeUtf8(ReadOnlySpan<byte> value)
    {
        return Encoding.UTF8.GetString(value);
    }
}
