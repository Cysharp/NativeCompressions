using System.Buffers;
using System.IO.Compression;
using static NativeCompressions.ZStandard.ZStdNativeMethods;

namespace NativeCompressions.ZStandard
{
    // zstd manual: https://raw.githack.com/facebook/zstd/release/doc/zstd_manual.html

    public unsafe partial struct ZStdEncoder : IDisposable
    {
        ZSTD_CCtx_s* context;
        int remaining;
        string? lastError;

        public int RemainingBuffer => remaining;
        public string? LastError => lastError;

        public ZStdEncoder()
        {
            this.context = ZSTD_createCStream();
            this.remaining = 0;
            this.lastError = null;
        }

        public ZStdEncoder(int compressionLevel)
        {
            this.context = ZSTD_createCStream();
            this.remaining = 0;
            this.lastError = null;
            SetCompressionLevel(compressionLevel);
        }

        public ZStdEncoder(CompressionLevel compressionLevel)
        {
            this.context = ZSTD_createCStream();
            this.remaining = 0;
            this.lastError = null;
            SetCompressionLevel(ConvertCompressionLevel(compressionLevel));
        }

        static void ThrowNoCompression()
        {
            throw new ArgumentException("NoCompression is not supported");
        }

        public void SetCompressionLevel(int level)
        {
            // ZSTD_c_compressionLevel = 100
            SetParameter(100, level);
        }

        public void SetParameter(int param, int level)
        {
            ValidateContext();

            var code = ZSTD_CCtx_setParameter(context, param, level);
            HandleError(code);
        }

        public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)
        {
            return CompressCore(source, destination, out bytesConsumed, out bytesWritten, isFinalBlock ? EndDirective.End : EndDirective.Continue);
        }

        public OperationStatus Flush(Span<byte> destination, out int bytesWritten)
        {
            return CompressCore(Array.Empty<byte>(), destination, out _, out bytesWritten, EndDirective.Flush);
        }

        public OperationStatus Close(Span<byte> destination, out int bytesWritten)
        {
            return CompressCore(Array.Empty<byte>(), destination, out _, out bytesWritten, EndDirective.End);
        }

        // when succeed, bytesConsumed = source.Length
        OperationStatus CompressCore(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, int endDirective)
        {
            ValidateContext();

            unsafe
            {
                bytesConsumed = 0;
                bytesWritten = 0;
                var availableOutput = destination.Length;

                fixed (byte* src = source)
                fixed (byte* dest = destination)
                {
                    var inBuffer = new ZSTD_inBuffer_s
                    {
                        src = src,
                        size = (nuint)source.Length,
                        pos = 0
                    };
                    var outBuffer = new ZSTD_outBuffer_s
                    {
                        dst = dest,
                        size = (nuint)destination.Length,
                        pos = 0
                    };

                    while (availableOutput > 0)
                    {
                        // @retrun: provides a minimum amount of data remaining to be flushed from internal buffers or an error code
                        var nb = ZSTD_compressStream2(context, &outBuffer, &inBuffer, endDirective);
                        if (IsError(nb))
                        {
                            lastError = GetErrorName(nb);
                            return OperationStatus.InvalidData;
                        }

                        bytesConsumed = (int)inBuffer.pos;
                        bytesWritten = (int)outBuffer.pos;

                        availableOutput -= bytesWritten;
                        remaining = (int)nb;

                        if (nb == 0 && bytesConsumed == source.Length)
                        {
                            return OperationStatus.Done;
                        }
                    }
                }
            }

            return OperationStatus.DestinationTooSmall;
        }

        void ValidateContext()
        {
            if (context == null)
            {
                throw new ObjectDisposedException("");
            }
        }

        public void Dispose()
        {
            if (context != null)
            {
                var code = ZSTD_freeCStream(context);
                context = null;
                lastError = null;
                remaining = 0;
                HandleError(code);
            }
        }

        internal static class EndDirective
        {
            /// <summary>
            /// collect more data, encoder decides when to output compressed result, for optimal compression ratio
            /// </summary>
            public const int Continue = 0;
            /// <summary>
            /// flush any data provided so far,
            /// it creates (at least) one new block, that can be decoded immediately on reception;
            /// frame will continue: any future data can still reference previously compressed data, improving compression.
            /// </summary>
            public const int Flush = 1;
            /// <summary>
            /// flush any remaining data _and_ close current frame.
            /// note that frame is only closed after compressed data is fully flushed (return value == 0).
            /// After that point, any additional data starts a new frame.
            /// note : each frame is independent (does not reference any content from previous frame).
            /// </summary>
            public const int End = 2;
        }
    }
}
