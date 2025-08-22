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


//var len = LZ4.GetMaxCompressedLength(999, LZ4FrameOptions.Default);

//Console.WriteLine(len);

//using var encoder = new LZ4Encoder(LZ4FrameOptions.Default);

//encoder.Compress();
//encoder.Compress();
// encoder.Flush();


var src = new byte[202400];
Random.Shared.NextBytes(src);

var dest = new byte[242164];

using var encoder = new LZ4Encoder();



var size = LZ4.GetMaxCompressedLengthForStreamingEncoder(src.Length, LZ4FrameOptions.Default);


var result = encoder.Compress(src, dest, out var bytesConsumed, out var bytesWritten, isFinalBlock: true);
Console.WriteLine(result);
Console.WriteLine("consumed:" + bytesConsumed);

var decompressed = LZ4.Decompress(dest.AsSpan(0, bytesWritten));

Console.WriteLine("Equal:" + decompressed.SequenceEqual(src));

