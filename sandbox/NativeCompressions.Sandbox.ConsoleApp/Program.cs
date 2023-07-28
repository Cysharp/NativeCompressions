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


var ms = new MemoryStream();
var lz4 = new LZ4Stream(ms, CompressionMode.Compress);





var bytes1 = Encoding.UTF8.GetBytes("あいうえおかきくけこ");
lz4.Write(bytes1);
lz4.Write(bytes1);
lz4.Write(bytes1);
lz4.Write(bytes1);
lz4.Write(bytes1);


lz4.Flush();



var xss = ms.ToArray();


var lz42 = new LZ4Stream(ms, CompressionMode.Decompress);

//lz42.Read(


Console.WriteLine(xss.Length);


var dest = new byte[1024];
if (!LZ4Decoder.TryDecompressFrame(xss, dest, out var written))
{
    Console.WriteLine("error");
}
else
{
    Console.WriteLine(Encoding.UTF8.GetString(dest, 0, written));
}