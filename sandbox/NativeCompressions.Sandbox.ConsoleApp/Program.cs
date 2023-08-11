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


var ms = new MemoryStream();
var stream = new ZStdStream(ms, CompressionLevel.Fastest);


stream.Write(bytes1);
stream.Write(bytes1);

stream.Dispose();

var data = ms.ToArray();

var dest = new byte[1024];
ZStdDecoder.TryDecompress(data, dest, out var written);

Console.WriteLine(Encoding.UTF8.GetString(dest.AsSpan(0, written)));