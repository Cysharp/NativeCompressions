using System.Buffers;
using System.Runtime.InteropServices;
using NativeCompressions.LZ4.Raw;
using static NativeCompressions.LZ4.Raw.NativeMethods;

namespace NativeCompressions.LZ4
{
    // https://github.com/lz4/lz4/blob/v1.9.4/lib/lz4.h
    // https://github.com/lz4/lz4/blob/v1.9.4/lib/lz4frame.h

    // spec
    // https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md
    // https://github.com/lz4/lz4/blob/dev/doc/lz4_Frame_format.md

    // manual
    // https://htmlpreview.github.io/?https://github.com/lz4/lz4/blob/master/doc/lz4_manual.html
    // https://htmlpreview.github.io/?https://github.com/lz4/lz4/blob/master/doc/lz4frame_manual.html
    public unsafe partial struct LZ4Decoder : IDisposable
    {
        LZ4F_dctx_s* context = default;
        bool disposed;

        public LZ4Decoder()
        {
            LZ4F_dctx_s* context = default;
            var code = LZ4F_createDecompressionContext(&context, LZ4.FrameVersion);
            HandleError(code);
            this.context = context;
        }

        public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
        {
            ValidateDisposed();

            unsafe
            {
                // LZ4F_decompress:
                // The nb of bytes consumed from srcBuffer will be written into *srcSizePtr (necessarily <= original value).
                // The nb of bytes decompressed into dstBuffer will be written into *dstSizePtr (necessarily <= original value).

                // @return: an hint of how many `srcSize` bytes LZ4F_decompress() expects for next call.
                // Schematically, it's the size of the current (or remaining) compressed block + header of next block.
                // Respecting the hint provides some small speed benefit, because it skips intermediate buffers.
                // This is just a hint though, it's always possible to provide any srcSize.
                // When a frame is fully decoded, @return will be 0(no more data expected).
                // When provided with more bytes than necessary to decode a frame,
                // LZ4F_decompress() will stop reading exactly at end of current frame, and @return 0.
                // If decompression failed, @return is an error code, which can be tested using LZ4F_isError().

                fixed (byte* src = &MemoryMarshal.GetReference(source))
                fixed (byte* dest = &MemoryMarshal.GetReference(destination))
                {
                    var consumed = (nuint)source.Length;
                    var written = (nuint)destination.Length;

                    var result = LZ4F_decompress(context, dest, &written, src, &consumed, null);
                    if (LZ4F_isError(result) != 0)
                    {
                        // require get message?
                        bytesConsumed = 0;
                        bytesWritten = 0;
                        return OperationStatus.InvalidData;
                    }

                    bytesConsumed = (int)consumed;
                    bytesWritten = (int)written;

                    if (result == 0)
                    {
                        if (bytesConsumed == source.Length)
                        {
                            return OperationStatus.Done;
                        }
                        else
                        {
                            return OperationStatus.DestinationTooSmall;
                        }
                    }

                    if (destination.Length == bytesWritten)
                    {
                        return OperationStatus.DestinationTooSmall;
                    }
                    else
                    {
                        return OperationStatus.NeedMoreData;
                    }
                }
            }
        }

        void ValidateDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("");
            }
        }

        public void Dispose()
        {
            if (context != null)
            {
                var code = LZ4F_freeDecompressionContext(context);
                HandleError(code);
                disposed = true;
            }
        }
    }
}
