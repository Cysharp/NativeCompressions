using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static NativeCompressions.ZStandard.ZStdNativeMethods;

namespace NativeCompressions.ZStandard
{
    // zstd manual: https://raw.githack.com/facebook/zstd/release/doc/zstd_manual.html


    public unsafe partial struct ZStdEncoder
    {
        ZSTD_CCtx_s* stream;

        public ZStdEncoder()
        {
            this.stream = ZSTD_createCStream();
        }

        public void SetCompressionLevel(int level)
        {
            // ZSTD_c_compressionLevel = 100
            SetParameter(100, level);
        }

        public void SetParameter(int param, int level)
        {
            var code = ZSTD_CCtx_setParameter(stream, param, level);
            HandleError(code);
        }

        public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)
        {

            unsafe
            {
                // TODO: consume all source bytes.

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

                    var directive = isFinalBlock ? EndDirective.End : EndDirective.Continue;

                    var nb = ZSTD_compressStream2(stream, &outBuffer, &inBuffer, directive);
                    HandleError(nb);

                    bytesConsumed = (int)inBuffer.pos;
                    bytesWritten = (int)outBuffer.pos;

                    return OperationStatus.Done;
                }
            }

            throw new NotImplementedException();
        }

        public OperationStatus Flush(Span<byte> destination, out int bytesWritten)
        {
            throw new NotImplementedException();
        }

        public OperationStatus Close(Span<byte> destination, out int bytesWritten)
        {
            unsafe
            {
                fixed (byte* src = Array.Empty<byte>())
                fixed (byte* dest = destination)
                {
                    var inBuffer = new ZSTD_inBuffer_s
                    {
                        src = src,
                        size = 0,
                        pos = 0
                    };
                    var outBuffer = new ZSTD_outBuffer_s
                    {
                        dst = dest,
                        size = (nuint)destination.Length,
                        pos = 0
                    };

                    var directive = EndDirective.End;

                    var nb = ZSTD_compressStream2(stream, &outBuffer, &inBuffer, directive);
                    HandleError(nb);

                    minimumBytesForFlush = (int)nb;
                    bytesWritten = (int)outBuffer.pos;

                    return OperationStatus.Done;
                }
            }

            throw new NotImplementedException();
        }

        // TODO: Reset for reuse.

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
