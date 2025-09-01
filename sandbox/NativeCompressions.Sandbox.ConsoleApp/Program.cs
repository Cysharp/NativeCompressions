//using K4os.Compression.LZ4.Streams;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NativeCompressions.LZ4;
using System.IO.Pipelines;
using System;
using System.Threading.Tasks;
using System.Threading;
using System.Buffers;
using Microsoft.Win32.SafeHandles;



var linkedCompressed = File.ReadAllBytes("silesia.tar.lz4");
var original = LZ4.Decompress(linkedCompressed);
var blockIndependenCompressed = LZ4.Compress(original);


//var parallelCompressedHandle = File.OpenRead("silesia.tar.lz4.p");
//var dest = new ArrayBufferPipeWriter();

//await LZ4.DecompressAsync(parallelCompressedHandle, dest);

//Console.WriteLine(dest.WrittenCount);
//Console.WriteLine(original.Length);

Console.WriteLine(LZ4.Version);

//using var fs = new FileStream("silesia.tar.lz4", FileMode.Open, FileAccess.Read, FileShare.Read, 1, useAsync: true);

//var filePipe = PipeReader.Create(fs);

//await LZ4.DecompressAsync(filePipe, dest);

//Console.WriteLine(dest.WrittenCount);



//await LZ4.CompressAsync(original, dest, maxDegreeOfParallelism: 1);

//var compressedBytes = dest.WrittenSpan.ToArray();


//var memoryPipe = PipeReader.Create(new MemoryStream(compressedBytes));

//var readResult = await memoryPipe.ReadAtLeastAsync(compressedBytes.Length);
//var readOnlySequence = readResult.Buffer;


//var dest2 = new ArrayBufferPipeWriter();
//await LZ4.DecompressAsync(readOnlySequence, dest2);

//Console.WriteLine(dest2.WrittenCount);
//Console.WriteLine(dest2.WrittenSpan.SequenceEqual(original));

