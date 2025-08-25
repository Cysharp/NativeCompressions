//using NativeCompressions.Lz4;
//using System;
//using System.Collections.Generic;
//using System.Diagnostics;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;

//namespace NativeCompressions.Tests;

//public class LZ4Test
//{
//    [Fact(Skip ="WIP")]
//    public void EncoderContinueCompress()
//    {
//        var src = new byte[1024];
//        var totalWrite = 0;
//        var span = src.AsSpan();
//        using var encoder = new LZ4Encoder();

//        //var written = encoder.Compress(EncodeUtf8("あいうえおあいうえおあいうえお"), span);
//        //totalWrite += written;
//        //span = span.Slice(written);

//        //written = encoder.Compress(EncodeUtf8("あいうえおあいうえおあいうえお"), span);
//        //totalWrite += written;
//        //span = span.Slice(written);

//        //written = encoder.Compress(EncodeUtf8("abcdefghijklmnopqrstu"), span);
//        //totalWrite += written;
//        //span = span.Slice(written);

//        //var compressed = src.AsSpan(0, totalWrite).ToArray();

//        //var dest = new byte[1024];

//        //using var decoder = new LZ4Decoder();

//        //written = decoder.Decompress(compressed, dest);


//        //var str = Encoding.UTF8.GetString(dest.AsSpan(0, written));

//        //str.Should().Be("あいうえおあいうえおあいうえおabcdefghijklmnopqrstu");
//    }

//    [Fact]
//    public void HC()
//    {
//        var bin = LZ4Encoder.CompressHC(EncodeUtf8("あいうえおあいうえおあいうえお"), LZ4HCCompressionLevel.Default);

//        byte[] dest = new byte[1024];
//        var ok = LZ4Decoder.TryDecompress(bin, dest, out var bytesWritten);
//        ok.Should().BeTrue();
//        var str = Encoding.UTF8.GetString(dest.AsSpan(0, bytesWritten));
//        str.Should().Be("あいうえおあいうえおあいうえお");
//    }


//    [Fact]
//    public void SimpleFrameCompress()
//    {
//        var bin = LZ4Encoder.CompressFrame(EncodeUtf8("あいうえおあいうえおあいうえお"));
//        var dest = new byte[1024];
//        LZ4Decoder.TryDecompressFrame(bin, dest, out var bytesWritten);

//        var str = Encoding.UTF8.GetString(dest.AsSpan(0, bytesWritten));
//        str.Should().Be("あいうえおあいうえおあいうえお");
//    }

//    [Fact]
//    public void Encoder()
//    {
//        using var encoder = new LZ4Encoder();

//        var foo = LZ4Encoder.GetMaxFrameCompressedLength(1024);

//        var dest = new byte[foo];
//        var slice = dest.AsSpan();
//        var totalWritten = 0;
//        var written = encoder.WriteHeader(slice);
//        totalWritten += written;
//        slice = slice.Slice(written);

//        written = encoder.Compress(EncodeUtf8("あいうえおあいうえおあいうえお"), slice);
//        totalWritten += written;
//        slice = slice.Slice(written);

//        written = encoder.Compress(EncodeUtf8("かきくけこかきくけこかきくけこ"), slice);
//        totalWritten += written;
//        slice = slice.Slice(written);

//        written = encoder.Flush(slice);
//        totalWritten += written;
//        slice = slice.Slice(written);

//        var bin = dest.AsSpan(0, totalWritten).ToArray();

//        var dest2 = new byte[foo];
//        LZ4Decoder.TryDecompressFrame(bin, dest2, out var bytesWritten);

//        var str = Encoding.UTF8.GetString(dest2.AsSpan(0, bytesWritten));
//        str.Should().Be("あいうえおあいうえおあいうえおかきくけこかきくけこかきくけこ");
//    }

//    [Fact]
//    public void FrameDecompressTest()
//    {
//        // TODO: write more data....!

//        var rand = new Random(143233);
//        var source = new byte[99999];
//        rand.NextBytes(source);
//        // source 

//        var compressSource = LZ4Encoder.CompressFrame(source);

//        {
//            using var decoder = new LZ4Decoder();
//            var dest = new byte[100]; // dest is small.
//            var status = decoder.Decompress(compressSource, dest, out var consumed, out var written);
//            status.Should().Be(System.Buffers.OperationStatus.DestinationTooSmall);
//        }
//        {
//            using var decoder = new LZ4Decoder();
//            var dest = new byte[999999]; // dest is full.
//            var status = decoder.Decompress(compressSource, dest, out var consumed, out var written);
//            status.Should().Be(System.Buffers.OperationStatus.Done);
//        }
//        {
//            using var decoder = new LZ4Decoder();
//            var src = compressSource.AsSpan(0, 1000); // src is small
//            var dest = new byte[999999];
//            var status = decoder.Decompress(src, dest, out var consumed, out var written);
//            status.Should().Be(System.Buffers.OperationStatus.NeedMoreData);
//        }
//    }

//    [DebuggerStepThrough]
//    byte[] EncodeUtf8(string value)
//    {
//        return Encoding.UTF8.GetBytes(value);
//    }
//}
