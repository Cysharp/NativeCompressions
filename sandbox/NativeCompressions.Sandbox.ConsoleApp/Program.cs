//using K4os.Compression.LZ4.Streams;
using NativeCompressions.ZStandard;
using System;
using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ZstdNet;
using NativeCompressions.LZ4;

var src = new byte[202400];
//Random.Shared.NextBytes(src);

src = Enumerable.Repeat((byte)'a', 202400).ToArray();

using var encoder = new LZ4Encoder();

var dest = new byte[encoder.GetMaxCompressedLength(src.Length, includingHeaderAndFooter: true)];
var bytesWritten = encoder.Compress(src, dest);
bytesWritten += encoder.Close(dest.AsSpan(bytesWritten));

Console.WriteLine(bytesWritten);

using var decoder = new LZ4Decoder();

var newSource = dest.AsSpan(0, bytesWritten).ToArray();
var newDest = new byte[10000];


// var options = new LZ4FrameOptions();



//var options = LZ4FrameOptions.Default with
//{
//    CompressionLevel = 10,
//    FrameInfo = with
//    {
//        DictionaryID = 1,
//        ContentSize = 100
//    }
//};


// var a = info with { DictionaryID = 1 };


var done = decoder.Decompress(newSource, newDest, out var consumed2, out var written2);

Console.WriteLine(done);
Console.WriteLine("consumed2:" + consumed2);
Console.WriteLine("written2:" + written2);


