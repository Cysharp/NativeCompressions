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

var path = @"silesia.tar";
var src = File.ReadAllBytes(path);



await LZ4.CompressAsync(src, writer, LZ4FrameOptions.Default, null);
var dest = ms.ToArray();

var ms2 = new MemoryStream();
var newWriter = PipeWriter.Create(ms2);

await LZ4.DecompressAsync(dest, newWriter, LZ4FrameOptions.Default);

var foo = ms2.ToArray();


Console.WriteLine(src.SequenceEqual(foo));

