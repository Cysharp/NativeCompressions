using K4os.Compression.LZ4.Streams;
using NativeCompressions.Lz4;
using NativeCompressions.ZStandard;
using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

unsafe
{


    Console.WriteLine(LZ4Encoder.GetMaxFrameCompressedLength(0));

    using var encoder = new LZ4Encoder();

    var dest = new byte[3]; // dest too small
    encoder.Compress(EncodeUtf8("あいうえおあいうえおあいうえお"), dest);







}

[DebuggerStepThrough]
byte[] EncodeUtf8(string value)
{
    return Encoding.UTF8.GetBytes(value);
}