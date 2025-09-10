using System.Buffers;
using static NativeCompressions.ZStandard.ZStdNativeMethods;

namespace NativeCompressions.ZStandard
{
    // zstd manual: https://raw.githack.com/facebook/zstd/release/doc/zstd_manual.html

    public unsafe partial struct ZStdDecoder : IDisposable
    {
        ZSTD_DCtx_s* context;
        string? lastError;

        public string? LastError => lastError;

        public ZStdDecoder()
        {
            this.context = ZSTD_createDStream();
            this.lastError = null;
        }

        public void SetParameter(int param, int value)
        {
            ValidateContext();

            var code = ZSTD_DCtx_setParameter(context, param, value);
            HandleError(code);
        }

        public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
        {
            ValidateContext();

            bytesConsumed = 0;
            bytesWritten = 0;

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

                // @return: 0 when a frame is completely decoded and fully flushed,
                // or an error code, which can be tested using ZSTD_isError(),
                // or any other value > 0, which means there is some decoding or flushing to do to complete current frame.
                var nb = ZSTD_decompressStream(context, &outBuffer, &inBuffer);
                if (IsError(nb))
                {
                    lastError = GetErrorName(nb);
                    return OperationStatus.InvalidData;
                }

                bytesConsumed = (int)inBuffer.pos;
                bytesWritten = (int)outBuffer.pos;

                if (nb == 0)
                {
                    return OperationStatus.Done;
                }

                // buffer is fully written
                if (outBuffer.pos == outBuffer.size)
                {
                    return OperationStatus.DestinationTooSmall;
                }

                // src is fully consumed
                if (inBuffer.pos == inBuffer.size)
                {
                    return OperationStatus.NeedMoreData;
                }
            }

            return OperationStatus.DestinationTooSmall;
        }


        void ValidateContext()
        {
            if (context == null)
            {
                throw new ObjectDisposedException(nameof(ZStdDecoder));
            }
        }

        public void Dispose()
        {
            if (context != null)
            {
                var code = ZSTD_freeDStream(context);
                context = null;
                lastError = null;
                // don't throw on disposing...!
                // HandleError(code);
            }
        }
    }
}
