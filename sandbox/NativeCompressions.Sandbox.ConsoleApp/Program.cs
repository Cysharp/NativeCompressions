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
using static System.Runtime.InteropServices.JavaScript.JSType;


//var source = new byte[] { 1, 2, 3, 4, 5 };
//byte[][] dataChunks = [source, source, source];

//// Streaming Compression
//IBufferWriter<byte> bufferWriter = default!;

//using var encoder = new LZ4Encoder();

//// Compress chunks(invoke Compress multiple times)
//foreach (var chunk in dataChunks)
//{
//    // get max size per streaming compress
//    var size = encoder.GetMaxCompressedLength(chunk.Length);

//    var buffer = bufferWriter.GetSpan(size);

//    // written size can be zero, meaning input data was just buffered.
//    var written = encoder.Compress(chunk, buffer);

//    bufferWriter.Advance(written);
//}

//// Finalize frame(need to get size of footer with buffered-data)
//var fotterWithBufferdDataSize = encoder.GetMaxCompressedLength(0);
//var finalBytes = bufferWriter.GetSpan(fotterWithBufferdDataSize);

//// need to call `Close` to write LZ4 frame footer
//int finalWritten = encoder.Close(finalBytes);

//bufferWriter.Advance(finalWritten);





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
// Socket socket = default!;

var source = ReadOnlyMemory<byte>.Empty;


// Parallel Compression from File to File
//using SafeFileHandle sourceHandle = File.OpenHandle("foo.bin");
//using var dest = new FileStream("foo.lz4", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, bufferSize: 1, useAsync: true);
//await LZ4.CompressAsync(sourceHandle, PipeWriter.Create(dest), maxDegreeOfParallelism: null);




using var ms = new MemoryStream();
using SafeFileHandle sourceHandle = File.OpenHandle("foo.lz4");
await LZ4.DecompressAsync(source, PipeWriter.Create(ms));

var decompressed = ms.ToArray();

//await LZ4.CompressAsync(original, dest, maxDegreeOfParallelism: 1);

//var compressedBytes = dest.WrittenSpan.ToArray();


//var memoryPipe = PipeReader.Create(new MemoryStream(compressedBytes));

//var readResult = await memoryPipe.ReadAtLeastAsync(compressedBytes.Length);
//var readOnlySequence = readResult.Buffer;


//var dest2 = new ArrayBufferPipeWriter();
//await LZ4.DecompressAsync(readOnlySequence, dest2);

//Console.WriteLine(dest2.WrittenCount);
//Console.WriteLine(dest2.WrittenSpan.SequenceEqual(original));

