//using K4os.Compression.LZ4.Streams;
using Microsoft.Win32.SafeHandles;
using NativeCompressions.LZ4;
using System;
using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NativeCompressions.ZStandard;


Console.WriteLine(ZStandard.Version);



//Console.WriteLine(LZ4.Version);

//var linkedCompressed = File.ReadAllBytes("silesia.tar.lz4");
//var original = LZ4.Decompress(linkedCompressed);
//var blockIndependenCompressed = LZ4.Compress(original);


//Console.ReadLine();

//var dest = new ArrayBufferPipeWriter();

////await LZ4.DecompressAsync(parallelCompressedHandle, dest);

////Console.WriteLine(dest.WrittenCount);
////Console.WriteLine(original.Length);

////Console.WriteLine(LZ4.Version);

////using var fs = new FileStream("silesia.tar.lz4", FileMode.Open, FileAccess.Read, FileShare.Read, 1, useAsync: true);

////var filePipe = PipeReader.Create(fs);

////await LZ4.DecompressAsync(filePipe, dest);

////Console.WriteLine(dest.WrittenCount);
//// Socket socket = default!;

//var source = ReadOnlyMemory<byte>.Empty;


//// Parallel Compression from File to File
////using SafeFileHandle sourceHandle = File.OpenHandle("foo.bin");
////using var dest = new FileStream("foo.lz4", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, bufferSize: 1, useAsync: true);
////await LZ4.CompressAsync(sourceHandle, PipeWriter.Create(dest), maxDegreeOfParallelism: null);


//var sw = Stopwatch.StartNew();
//await LZ4.CompressAsync(original, dest, maxDegreeOfParallelism: null);

//Console.WriteLine(sw.Elapsed.TotalMilliseconds + "ms");

////var compressedBytes = dest.WrittenSpan.ToArray();


////var memoryPipe = PipeReader.Create(new MemoryStream(compressedBytes));

////var readResult = await memoryPipe.ReadAtLeastAsync(compressedBytes.Length);
////var readOnlySequence = readResult.Buffer;


////var dest2 = new ArrayBufferPipeWriter();
////await LZ4.DecompressAsync(readOnlySequence, dest2);

////Console.WriteLine(dest2.WrittenCount);
////Console.WriteLine(dest2.WrittenSpan.SequenceEqual(original));

