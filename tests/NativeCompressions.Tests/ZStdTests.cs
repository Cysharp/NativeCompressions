using NativeCompressions.ZStandard;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NativeCompressions.Tests;

public class ZStdTests
{
    [Fact]
    public void MaxCompressedLengthTest()
    {
        for (int i = 0; i < 10000; i++)
        {
            var cs = ZStdEncoder.GetMaxCompressedLength(i);
            var original = (int)ZStdNativeMethods.ZSTD_compressBound((nuint)i);
            cs.Should().Be(original);
        }
    }

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

    [Fact]
    public void EncoderTestCases()
    {
        // OperationStatus pattern: InvalidData, Done, DestinationTooSmall.

        // standard
        var text = "あいうえおあいうえおあいうえおかきくけこ";
        var bin = EncodeUtf8(text);

        // Standard OK.
        {
            using var encoder = new ZStdEncoder();

            var dest = new byte[1024];
            encoder.Compress(bin, dest, out var bytesConsumed, out var bytesWritten, isFinalBlock: true).Should().Be(OperationStatus.Done);

            DecodeUtf8(ZStdDecoder.Decompress(dest.AsSpan(0, bytesWritten))).Should().Be(text);
        }

        // DestinationTooSmall
        {
            using var encoder = new ZStdEncoder();

            var dest = new byte[10];
            encoder.Compress(bin, dest, out var bytesConsumed, out var bytesWritten, isFinalBlock: true).Should().Be(OperationStatus.DestinationTooSmall);

            var newDest = new byte[dest.Length + encoder.RemainingBuffer];
            dest.CopyTo(newDest.AsSpan());

            // one more call Close.
            encoder.Close(newDest.AsSpan(bytesWritten), out var moreWritten).Should().Be(OperationStatus.Done);
            var totalWritten = bytesWritten + moreWritten;
            totalWritten.Should().Be(newDest.Length);

            DecodeUtf8(ZStdDecoder.Decompress(newDest)).Should().Be(text);
        }

        // DestinationTooSmall2
        {
            using var encoder = new ZStdEncoder();

            var dest = new byte[10];
            encoder.Compress(bin, dest, out var bytesConsumed, out var bytesWritten, isFinalBlock: false).Should().Be(OperationStatus.Done);
            bytesWritten.Should().Be(0); // not flushed.
            encoder.Close(dest, out bytesWritten).Should().Be(OperationStatus.DestinationTooSmall);

            var newDest = new byte[dest.Length + encoder.RemainingBuffer];
            dest.CopyTo(newDest.AsSpan());

            // one more call Close.
            encoder.Close(newDest.AsSpan(bytesWritten), out var moreWritten).Should().Be(OperationStatus.Done);
            var totalWritten = bytesWritten + moreWritten;
            totalWritten.Should().Be(newDest.Length);

            var moreDest = new byte[1024];
            ZStdDecoder.TryDecompress(newDest, moreDest, out var writtenFinal).Should().BeTrue();
            DecodeUtf8(moreDest.AsSpan(0, writtenFinal)).Should().Be(text);
        }

        // too large input 1
        {
            using var encoder = new ZStdEncoder();

            var rand = new Random(9999);
            var source = new byte[10000000];
            rand.NextBytes(source);
            
            
            encoder.Compress(source, dest, out var consumed, out var written, true);



        }


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
