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
using System.IO.Pipelines;

var ms = new MemoryStream();
var writer = PipeWriter.Create(ms);

var path = @"C:\Users\S04451\Downloads\silesia.tar";
var src = File.ReadAllBytes(path);

var dest = new byte[LZ4.GetMaxCompressedLength(src.Length, LZ4FrameOptions.Default)];

var sw = Stopwatch.StartNew();

//var written1 = K4os.Compression.LZ4.LZ4Codec.Encode(src, dest);

await LZ4.CompressAsync(src, writer, LZ4FrameOptions.Default, null);

//var written2 = LZ4.Compress(src, dest);
//Console.WriteLine(written1);
//Console.WriteLine(written2);
// var a = ms.ToArray();
Console.WriteLine(sw.ElapsedMilliseconds + "ms");


var more = new byte[src.Length];

var decoder = new LZ4Decoder();
dest = ms.ToArray();

decoder.Decompress(dest/*.AsSpan(0, written2)*/, more, out var a, out var b);
Console.WriteLine(src.SequenceEqual(more)); // ???




