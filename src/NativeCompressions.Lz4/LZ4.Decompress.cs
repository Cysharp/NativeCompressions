//using NativeCompressions.LZ4.Raw;
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;

//namespace NativeCompressions.LZ4;

//public static partial class LZ4
//{
//    // PipeReader? ReadOnlySequence<T>? Stream?
//    public static unsafe byte[] Decompress(ReadOnlySpan<byte> source)
//    {
//        // LZ4F_decompress
//        LZ4F_dctx_s* dctx = default; // TODO: create from DecompressOptions?
//        var code = LZ4F_createDecompressionContext(&dctx, FrameVersion);
//        HandleErrorCode(code);
//        // LZ4F_getFrameInfo



//        var destination = new byte[60000]; // TODO: which bytes?

//        var totalWritten = 0;
//        fixed (byte* src = source)
//        fixed (byte* dest = destination)
//        {

//            // LZ4F_getFrameInfo()
//            var consumedSourceLength = (nuint)source.Length;
//            var writtenDestinationLength = (nuint)destination.Length;


//            //LZ4F_frameInfo_t finfo = default;
//            //var code2 = LZ4F_getFrameInfo(dctx, &finfo, src, &consumedSourceLength);

//            // an hint of how many `srcSize` bytes LZ4F_decompress() expects for next call.

//            var hintOrErrorCode = LZ4F_decompress(dctx, dest, &writtenDestinationLength, src, &consumedSourceLength, null);
//            totalWritten += (int)writtenDestinationLength;




//            if (hintOrErrorCode == 0)
//            {
//                // success decompression.
//                return destination.AsSpan(0, totalWritten).ToArray();
//            }

//            if (LZ4F_isError(hintOrErrorCode) != 0)
//            {
//                LZ4.HandleErrorCode(hintOrErrorCode);
//                // error...
//            }
//        }

//        return destination;
//    }

//}
