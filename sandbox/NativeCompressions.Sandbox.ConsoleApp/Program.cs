//using K4os.Compression.LZ4.Streams;
using NativeCompressions.ZStandard;
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
using System;
using System.Threading.Tasks;
using System.Threading;
using System.Buffers;







//using var fs = new FileStream("silesia.tar.lz4", FileMode.Open, FileAccess.Read, FileShare.Read, 1, useAsync: true);

//var filePipe = PipeReader.Create(fs);
var dest = new ArrayBufferPipeWriter();

//await LZ4.DecompressAsync(filePipe, dest);

//Console.WriteLine(dest.WrittenCount);

var rawFilePath = "";

var original = File.ReadAllBytes(rawFilePath);

await LZ4.CompressAsync(original, dest, maxDegreeOfParallelism: null);

var compressedBytes = dest.WrittenSpan.ToArray();


var memoryPipe = PipeReader.Create(new MemoryStream(compressedBytes));

var dest2 = new ArrayBufferPipeWriter();
await LZ4.DecompressAsync(memoryPipe, dest2);

Console.WriteLine(dest2.WrittenCount);
Console.WriteLine(dest2.WrittenSpan.SequenceEqual(original));

