//using System.Buffers;
//using System.IO.Compression;
//using static NativeCompressions.ZStandard.ZStdNativeMethods;

//namespace NativeCompressions.ZStandard
//{
//    // zstd manual: https://raw.githack.com/facebook/zstd/release/doc/zstd_manual.html

//    public unsafe partial struct ZStdEncoder : IDisposable
//    {
//        const int CompressionLevelParam = 100; // ZSTD_c_compressionLevel

//        ZSTD_CCtx_s* context;
//        int remaining;
//        string? lastError;

//        public int RemainingBuffer => remaining;
//        public string? LastError => lastError;

//        public ZStdEncoder()
//        {
//            this.context = ZSTD_createCStream();
//            this.remaining = 0;
//            this.lastError = null;
//        }

//        public ZStdEncoder(int compressionLevel)
//        {
//            this.context = ZSTD_createCStream();
//            this.remaining = 0;
//            this.lastError = null;
//            SetCompressionLevel(compressionLevel);
//        }

//        public ZStdEncoder(CompressionLevel compressionLevel)
//        {
//            this.context = ZSTD_createCStream();
//            this.remaining = 0;
//            this.lastError = null;
//            SetCompressionLevel(ConvertCompressionLevel(compressionLevel));
//        }

//        static void ThrowNoCompression()
//        {
//            throw new ArgumentException("NoCompression is not supported");
//        }

//        public void SetCompressionLevel(int level)
//        {
//            SetParameter(CompressionLevelParam, level);
//        }

//        public void SetCompressionLevel(CompressionLevel compressionLevel)
//        {
//            SetParameter(CompressionLevelParam, ConvertCompressionLevel(compressionLevel));
//        }

//        public void SetParameter(int param, int value)
//        {
//            ValidateContext();

//            var code = ZSTD_CCtx_setParameter(context, param, value);
//            HandleError(code);
//        }

//        public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)
//        {
//            return Compress(source, destination, out bytesConsumed, out bytesWritten, isFinalBlock ? EndDirective.End : EndDirective.Continue);
//        }

//        public OperationStatus Flush(Span<byte> destination, out int bytesWritten)
//        {
//            return Compress(Array.Empty<byte>(), destination, out _, out bytesWritten, EndDirective.Flush);
//        }

//        public OperationStatus Close(Span<byte> destination, out int bytesWritten)
//        {
//            return Compress(Array.Empty<byte>(), destination, out _, out bytesWritten, EndDirective.End);
//        }

//        // when succeed, bytesConsumed = source.Length
//        public unsafe OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, EndDirective endDirective)
//        {
//            ValidateContext();

//            bytesConsumed = 0;
//            bytesWritten = 0;

//            fixed (byte* src = source)
//            fixed (byte* dest = destination)
//            {
//                var inBuffer = new ZSTD_inBuffer_s
//                {
//                    src = src,
//                    size = (nuint)source.Length,
//                    pos = 0
//                };
//                var outBuffer = new ZSTD_outBuffer_s
//                {
//                    dst = dest,
//                    size = (nuint)destination.Length,
//                    pos = 0
//                };

//                // @retrun: provides a minimum amount of data remaining to be flushed from internal buffers or an error code
//                var nb = ZSTD_compressStream2(context, &outBuffer, &inBuffer, (int)endDirective);
//                if (IsError(nb))
//                {
//                    lastError = GetErrorName(nb);
//                    return OperationStatus.InvalidData;
//                }

//                bytesConsumed = (int)inBuffer.pos;
//                bytesWritten = (int)outBuffer.pos;
//                remaining = (int)nb;

//                if (nb == 0 && bytesConsumed == source.Length)
//                {
//                    return OperationStatus.Done;
//                }
//            }

//            return OperationStatus.DestinationTooSmall;
//        }

//        void ValidateContext()
//        {
//            if (context == null)
//            {
//                throw new ObjectDisposedException(nameof(ZStdEncoder));
//            }
//        }

//        public void Dispose()
//        {
//            if (context != null)
//            {
//                var code = ZSTD_freeCStream(context);
//                context = null;
//                lastError = null;
//                remaining = 0;
//                // don't throw on disposing...!
//                // HandleError(code);
//            }
//        }
//    }
//}
