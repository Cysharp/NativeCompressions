using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static NativeCompressions.Lz4.Lz4NativeMethods;

namespace NativeCompressions.Lz4
{
    // https://github.com/lz4/lz4/blob/v1.9.4/lib/lz4.h
    // https://github.com/lz4/lz4/blob/v1.9.4/lib/lz4frame.h

    // spec
    // https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md
    // https://github.com/lz4/lz4/blob/dev/doc/lz4_Frame_format.md

    // manual
    // https://htmlpreview.github.io/?https://github.com/lz4/lz4/blob/master/doc/lz4_manual.html
    // https://htmlpreview.github.io/?https://github.com/lz4/lz4/blob/master/doc/lz4frame_manual.html
    public unsafe partial struct LZ4Encoder : IDisposable
    {
        LZ4F_cctx_s* context;
        bool writeBegin;
        bool writeEnd;
        bool disposed;

        public LZ4Encoder()
        {
            LZ4F_cctx_s* ptr = default;
            var code = LZ4F_createCompressionContext(&ptr, FrameVersion);
            HandleError(code);
            this.context = ptr;
        }

        public unsafe int WriteHeader(Span<byte> destination, LZ4FrameHeader? header = null)
        {
            ValidateDisposed();
            ValidateFlushed();

            fixed (byte* dest = &MemoryMarshal.GetReference(destination))
            {
                LZ4F_preferences_t preference;
                LZ4F_preferences_t* preferencePtr = null;
                if (header != null)
                {
                    var v = header.Value;
                    preference = Unsafe.As<LZ4FrameHeader, LZ4F_preferences_t>(ref v);
                    preferencePtr = &preference;
                }

                var writtenOrErrorCode = LZ4F_compressBegin(context, dest, (nuint)destination.Length, preferencePtr);
                HandleError(writtenOrErrorCode);

                writeBegin = true;
                return (int)writtenOrErrorCode;
            }
        }

        // TODO:
        // public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock);
        public unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            ValidateDisposed();
            ValidateFlushed();

            fixed (byte* src = &MemoryMarshal.GetReference(source))
            fixed (byte* destPtr = &MemoryMarshal.GetReference(destination))
            {
                var dest = destPtr;
                int writtenSize = 0;
                // Write header
                if (!writeBegin)
                {
                    var code = LZ4F_compressBegin(context, dest, (nuint)destination.Length, null);
                    HandleError(code);
                    writeBegin = true;

                    writtenSize += (int)code;
                    dest += writtenSize;
                    destination = destination.Slice(writtenSize);
                }

                // Write block

                // bytes consumed, bytes written

                var writtenOrErrorCode = LZ4F_compressUpdate(context, dest, (nuint)destination.Length, src, (nuint)source.Length, null);
                // TODO: handle zero.
                HandleError(writtenOrErrorCode);

                writtenSize += (int)writtenOrErrorCode;
                return writtenSize;
            }
        }

        public unsafe int Flush(Span<byte> destination)
        {
            ValidateDisposed();
            ValidateFlushed();

            fixed (byte* destPtr = &MemoryMarshal.GetReference(destination))
            {
                var dest = destPtr;
                int writtenSize = 0;

                if (!writeBegin)
                {
                    var code = LZ4F_compressBegin(context, dest, (nuint)destination.Length, null);
                    HandleError(code);
                    writeBegin = true;

                    writtenSize += (int)code;
                    dest += writtenSize;
                    destination = destination.Slice(writtenSize);
                }

                // @return : nb of bytes written into dstBuffer, necessarily >= 4 (endMark), or an error code if it fails (which can be tested using LZ4F_isError())
                var writtenOrErrorCode = LZ4F_compressEnd(context, dest, (nuint)destination.Length, null);
                HandleError(writtenOrErrorCode);
                writtenSize += (int)writtenOrErrorCode;

                writeEnd = true;
                return writtenSize;
            }
        }

        void HandleError(nuint code)
        {
            if (LZ4F_isError(code) != 0)
            {
                // -11 == "ERROR_dstMaxSize_tooSmall"
                var error = GetErrorName(code);
                throw new InvalidOperationException(error);
            }
        }

        void ValidateFlushed()
        {
            if (writeEnd)
            {
                throw new InvalidOperationException("Frame is already flushed.");
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
                var code = LZ4F_freeCompressionContext(context);
                HandleError(code);
                disposed = true;
            }
        }
    }
}
