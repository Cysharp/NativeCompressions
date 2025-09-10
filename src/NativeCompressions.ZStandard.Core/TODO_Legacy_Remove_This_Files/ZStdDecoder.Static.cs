//using System.Buffers;
//using static NativeCompressions.ZStandard.ZStdNativeMethods;

//namespace NativeCompressions.ZStandard
//{
//    public unsafe partial struct ZStdDecoder
//    {
//        public static string Version => ZStdEncoder.Version;
//        public static uint VersionNumber => ZStdEncoder.VersionNumber;

//        public static unsafe bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
//        {
//            fixed (byte* src = source)
//            fixed (byte* dest = destination)
//            {
//                // @return : the number of bytes decompressed into `dst` (&lt;= `dstCapacity`),
//                // or an errorCode if it fails (which can be testedvar  using ZSTD_isError()).
//                var codeOrWritten = ZSTD_decompress(dest, (nuint)destination.Length, src, (nuint)source.Length);
//                if (IsError(codeOrWritten))
//                {
//                    bytesWritten = 0;
//                    return false;
//                }

//                bytesWritten = (int)codeOrWritten;
//                return true;
//            }
//        }

//        public static unsafe byte[] Decompress(ReadOnlySpan<byte> source, bool preferUseFrameContentSize = true)
//        {
//            if (!preferUseFrameContentSize)
//            {
//                return StreamingDecompress(source);
//            }

//            fixed (byte* src = source)
//            {
//                const ulong ZSTD_CONTENTSIZE_UNKNOWN = unchecked(0UL - 1);
//                const ulong ZSTD_CONTENTSIZE_ERROR = unchecked(0UL - 2);
//                var size = ZSTD_getFrameContentSize(src, (nuint)source.Length);
//                if (size == ZSTD_CONTENTSIZE_UNKNOWN)
//                {
//                    // throw new InvalidOperationException("Content size is unknown.");
//                    return StreamingDecompress(source);
//                }
//                else if (size == ZSTD_CONTENTSIZE_ERROR)
//                {
//                    // throw new InvalidOperationException("Content size error.");
//                    return StreamingDecompress(source);
//                }

//                var destination = new byte[checked((int)size)];
//                fixed (byte* dest = destination)
//                {
//                    // @return : the number of bytes decompressed into `dst` (&lt;= `dstCapacity`),
//                    // or an errorCode if it fails (which can be tested using ZSTD_isError()).
//                    var codeOrWritten = ZSTD_decompress(dest, (nuint)destination.Length, src, (nuint)source.Length);
//                    HandleError(codeOrWritten);

//                    if ((int)codeOrWritten != destination.Length)
//                    {
//                        throw new InvalidOperationException($"Frame header(content-size) and decompressed length is different. content-size: {(int)size}, decompressed: {(int)codeOrWritten}");
//                    }

//                    return destination;
//                }
//            }
//        }

//        static unsafe byte[] StreamingDecompress(ReadOnlySpan<byte> source)
//        {
//            using var decoder = new ZStdDecoder();
//            using var sequence = new ArraySequence(ZStdStream.DecompressOutputLimit); // use recommended size for output in default
//            var dest = sequence.CurrentSpan;
//            while (true)
//            {
//                var status = decoder.Decompress(source, dest, out var bytesConsumed, out var bytesWritten);
//                source = source.Slice(bytesConsumed);

//                switch (status)
//                {
//                    case OperationStatus.Done:
//                        return sequence.ToArrayAndDispose(bytesWritten);
//                    case OperationStatus.DestinationTooSmall:
//                        dest = sequence.AllocateNextBlock(bytesWritten);
//                        break;
//                    case OperationStatus.NeedMoreData:
//                    case OperationStatus.InvalidData:
//                    default:
//                        throw new InvalidOperationException($"Decompress result is {status}, source is invalid.");
//                }
//            }
//        }

//        static bool IsError(nuint code)
//        {
//            return ZSTD_isError(code) != 0;
//        }

//        static unsafe string GetErrorName(nuint code)
//        {
//            var name = (sbyte*)ZSTD_getErrorName(code);
//            return new string(name);
//        }

//        static void HandleError(nuint code)
//        {
//            if (ZSTD_isError(code) != 0)
//            {
//                var error = GetErrorName(code);
//                throw new InvalidOperationException(error);
//            }
//        }
//    }
//}
