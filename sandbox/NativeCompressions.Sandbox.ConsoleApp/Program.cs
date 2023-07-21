using K4os.Compression.LZ4.Streams;
using NativeCompressions.Lz4;
using NativeCompressions.ZStandard;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

unsafe
{
    var data = EncodeUtf8("あいうえおあいうえおあいうえお");

    var bin = LZ4Encoder.CompressFrame(EncodeUtf8("あいうえおあいうえおあいうえお"));

    // 196608
    // 200000

    var dest = new byte[200000];
    LZ4Encoder.TryDecompressFrame(bin, dest, out var bytesWritten);


    var dest2 = dest.AsSpan(0, data.Length);


    var str = Encoding.UTF8.GetString(dest2);
    Console.WriteLine(str);



    // str.Should().Be("あいうえおあいうえおあいうえお");







}

byte[] EncodeUtf8(string value)
{
    return Encoding.UTF8.GetBytes(value);
}