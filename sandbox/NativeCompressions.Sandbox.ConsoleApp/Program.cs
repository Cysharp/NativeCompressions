//using K4os.Compression.LZ4.Streams;
using NativeCompressions.Lz4;
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





var seq = new ArraySequence(ushort.MaxValue);
var span = seq.CurrentSpan;
for (int i = 0; i <= 11; i++)
{
    Console.WriteLine($"{i} {span.Length} {seq.length + span.Length} {Array.MaxLength}");
    if (i != 11)
    {
        span = seq.AllocateNextBlock(span.Length);
    }
}