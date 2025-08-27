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

CancellationTokenSource cts = new();
var src = new Pipe();
var dest = new Pipe();




var t = Task.Run(async () =>
{
    await LZ4.CompressAsync(src.Reader, dest.Writer);
});



await src.Writer.WriteAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });

//Console.ReadLine();
await src.Writer.WriteAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });

//Console.ReadLine();

src.Writer.Complete();


await t;


var a = await dest.Reader.ReadAsync();
// dest.Reader.AdvanceTo(a.Buffer.End);



//var b = dest.Reader.TryRead(out var result2);

var hogemoge = a.Buffer.ToArray();

var hugahgua = LZ4.Decompress(hogemoge);

Console.ReadLine();