using NativeCompressions.ZStandard;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

unsafe
{
    var p = (sbyte*)NativeCompressions.Lz4.Lz4NativeMethods.LZ4_versionString();
    var version = new string(p);
    Console.WriteLine("LZ4: " + version);
}


unsafe
{
    var p = (sbyte*)NativeCompressions.ZStandard.ZStdNativeMethods.ZSTD_versionString();
    var version = new string(p);
    Console.WriteLine("ZSTD: " + version);
}


// Struct/BrotliEncoder
// BrotliEncoder


// BrotliEncoder.GetMaxCompressedLength

// BrotliEncoder.TryCompress
// BrotliDecoder.TryDecompress()


//Console.WriteLine(ZStandard.Version);


//var a = ZStandard.GetErrorName((UIntPtr)100);
//Console.WriteLine(a);

////var libzstdHandle = NativeLibrary.Load("libzstd.dll");

////unsafe
////{
////    using var ctx = new CompressionStreamContext();

////    var source = File.ReadAllBytes(@"libzstd.dll");
////    var dest = new byte[300000];

////    var foo = ctx.Write(source, dest);


////    // 

////    Console.WriteLine("Foo2");


////}
////public static unsafe class NativeMethods
////{



////    static readonly IntPtr libzHandle;

////    public static readonly delegate*<UIntPtr> ZSTD_versionNumber;

////    static NativeMethods()
////    {
////        libzHandle = NativeLibrary.Load("libzstd.dll");
////        ZSTD_versionNumber = (delegate*<UIntPtr>)NativeLibrary.GetExport(libzHandle, nameof(ZSTD_versionNumber));




////    }

////}
////var input = new byte[] { 1, 10, 10 };


////var comp = ZStandard.Compress(input);
////var bin = ZStandard.Decompress(comp);