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


//var len = LZ4.GetMaxCompressedLength(999, LZ4FrameOptions.Default);

//Console.WriteLine(len);

//using var encoder = new LZ4Encoder(LZ4FrameOptions.Default);

//encoder.Compress();
//encoder.Compress();
// encoder.Flush();

var opt = new LZ4FrameOptions
{
    FrameInfo = new LZ4FrameInfo
    {
        BlockSizeID = BlockSizeId.Max64KB,
        BlockMode = BlockMode.BlockIndepent
    }
};

var len3 = LZ4.GetMaxCompressedLength(100, LZ4FrameOptions.Default);
var len4 = LZ4.GetMaxCompressedLengthInFrame(100, opt);

Console.WriteLine("MaxCompressed " + len3);
Console.WriteLine("MaxCompressedInFrame " + len4);


//Console.WriteLine(len3);


//Console.WriteLine(LZ4.Version);
//Console.WriteLine(LZ4.VersionNumber);
//Console.WriteLine(LZ4.FrameVersion);