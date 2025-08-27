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


var path = @"silesia.tar";

var handle = File.OpenHandle(path);

var sw = Stopwatch.StartNew();
var cts = new CancellationTokenSource();
using (var destFile = new FileStream("silesia.tar.lz4", FileMode.Create, FileAccess.Write, FileShare.None, 1, useAsync: true))
{
    await LZ4.CompressAsync(handle, PipeWriter.Create(destFile), maxDegreeOfParallelism: null);
}
Console.WriteLine(sw.ElapsedMilliseconds + "ms");

Console.WriteLine("done?");