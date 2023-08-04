using System;
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

    //typedef enum {
    //    ZSTD_e_continue = 0, /* collect more data, encoder decides when to output compressed result, for optimal compression ratio */
    //    ZSTD_e_flush = 1,    /* flush any data provided so far,
    //                    * it creates (at least) one new block, that can be decoded immediately on reception;
    //                    * frame will continue: any future data can still reference previously compressed data, improving compression.
    //                    * note : multithreaded compression will block to flush as much output as possible. */
    //    ZSTD_e_end = 2       /* flush any remaining data _and_ close current frame.
    //                    * note that frame is only closed after compressed data is fully flushed (return value == 0).
    //                    * After that point, any additional data starts a new frame.
    //                    * note : each frame is independent (does not reference any content from previous frame).
    //                    : note : multithreaded compression will block to flush as much output as possible. */
    //} ZSTD_EndDirective;
        

    public unsafe partial struct ZStdEncoder
    {
        public ZStdEncoder()
        {
            //and ZSTD_freeCStream
            var b = ZSTD_createCStream();

            // ZSTD_CStreamInSize();
            // ZSTD_compressStream2
            //ZSTD_compressStream(
        }
    }

    // using ZSTD_compressCCtx, ZSTD_freeCCtx is best for performance?
    public unsafe class ZStdCompressionContext : IDisposable
    {
        private bool disposedValue;

        public ZStdCompressionContext()
        {

            // using ZSTD_compressCCtx, ZSTD_freeCCtx is best for performance?
            var a = ZSTD_createCCtx();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~ZStdCompressionContext()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
