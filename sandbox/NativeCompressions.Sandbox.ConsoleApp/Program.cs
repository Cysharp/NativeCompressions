//using K4os.Compression.LZ4.Streams;
using NativeCompressions.Lz4;
using NativeCompressions.ZStandard;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ZstdNet;

var bytes1 = Encoding.UTF8.GetBytes("あいうえおかきくけこ");

//var ms = new MemoryStream();
//var compressionStream = new ZstdNet.CompressionStream(ms);
//compressionStream.Write(bytes1);
//compressionStream.Flush();
//compressionStream.Dispose();



var enc = new ZStdEncoder();



var dest = new byte[1024];
var slice = dest.AsSpan();
var totalWritten = 0;
enc.Compress(bytes1, slice, out var consumed, out var written, false);
slice = dest.AsSpan(written);
totalWritten += written;


enc.Compress(bytes1, slice, out consumed, out written, false);
slice = dest.AsSpan(written);
totalWritten += written;

enc.Compress(bytes1, slice, out consumed, out written, false);
slice = dest.AsSpan(written);
totalWritten += written;

enc.Compress(bytes1, slice, out consumed, out written, false);
slice = dest.AsSpan(written);
totalWritten += written;

enc.End(slice, out consumed, out written);
totalWritten += written;

Console.WriteLine(  totalWritten);
var foo = dest.AsSpan(0, totalWritten).ToArray();
var dest2 = new byte[1024];
//var tako = new ZstdNet.Decompressor().Unwrap(foo, dest2, false);

//var sss = Encoding.UTF8.GetString(dest2.AsSpan(0, tako));
//Console.WriteLine(sss);


ZStdEncoder.TryDecompress(foo, dest2, out var written2);


var sss = Encoding.UTF8.GetString(dest2.AsSpan(0, written2));
Console.WriteLine(sss);



//lz4.Write(bytes1);
//lz4.Write(bytes1);
//lz4.Write(bytes1);
//lz4.Write(bytes1);
//lz4.Write(bytes1);


//lz4.Flush();



//var xss = ms.ToArray();


//ms.Position = 0;
//var lz42 = new LZ4Stream(ms, CompressionMode.Decompress);

////lz42.Read(









//var dest = new byte[1024];


//var written = lz42.Read(dest);




//Console.WriteLine(Encoding.UTF8.GetString(dest, 0, written));
