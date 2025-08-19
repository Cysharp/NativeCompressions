using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeCompressions.LZ4.Raw;
using static NativeCompressions.LZ4.Raw.NativeMethods;

namespace NativeCompressions.LZ4
{
    // https://github.com/lz4/lz4/blob/v1.10.0/lib/lz4.h
    // https://github.com/lz4/lz4/blob/v1.10.0/lib/lz4frame.h

    // spec
    // https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md
    // https://github.com/lz4/lz4/blob/dev/doc/lz4_Frame_format.md

    // manual
    // https://htmlpreview.github.io/?https://github.com/lz4/lz4/blob/master/doc/lz4_manual.html
    // https://htmlpreview.github.io/?https://github.com/lz4/lz4/blob/master/doc/lz4frame_manual.html
    
    // cctx = Compression Context
    // dctx = Decompression Context
    // CDict = Compression Dictionary

    public unsafe partial struct LZ4Encoder : IDisposable
    {
        LZ4F_cctx_s* context;
        LZ4FrameOptions? header; // LZ4F_preferences_t
        LZ4CompressionDictionary? compressionDictionary;

        bool writeBegin;
        bool writeEnd;
        bool disposed;

        public bool IsWrittenHeader => writeBegin;
        public bool IsWrittenFooter => writeEnd;

        public LZ4Encoder()
            : this(null, null)
        {
        }

        public LZ4Encoder(LZ4FrameOptions? options, LZ4CompressionDictionary? compressionDictionary)
        {
            LZ4F_cctx_s* ptr = default;
            var code = LZ4F_createCompressionContext(&ptr, LZ4.FrameVersion);
            HandleError(code);
            this.context = ptr;
            this.header = options;
            this.compressionDictionary = compressionDictionary;
        }

        unsafe int WriteHeader(Span<byte> destination)
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
                    preference = Unsafe.As<LZ4FrameOptions, LZ4F_preferences_t>(ref v);
                    preferencePtr = &preference;
                }


                

                // TODO:
                // LZ4F_compressBegin
                // LZ4F_compressBegin_usingCDict
                // LZ4F_compressBegin_usingDict
                // LZ4F_compressBegin_usingDictOnce

                // LZ4F_compressBegin_usingDictOnce(

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

            var writtenSize = 0;

            // Write header
            if (!writeBegin)
            {
                var size = WriteHeader(destination);
                destination = destination.Slice(size);
                writtenSize += size;
            }

            fixed (byte* src = &MemoryMarshal.GetReference(source))
            fixed (byte* dest = &MemoryMarshal.GetReference(destination))
            {




                // Write block
                var writtenOrErrorCode = LZ4F_compressUpdate(context, dest, (nuint)destination.Length, src, (nuint)source.Length, null);
                HandleError(writtenOrErrorCode);

                writtenSize += (int)writtenOrErrorCode;
                return writtenSize;
            }
        }

        // Finish()


        public unsafe int Flush(Span<byte> destination)
        {
            ValidateDisposed();
            ValidateFlushed();

            var writtenSize = 0;

            // Write header
            if (!writeBegin)
            {
                var size = WriteHeader(destination);
                destination = destination.Slice(size);
                writtenSize += size;
            }

            fixed (byte* dest = &MemoryMarshal.GetReference(destination))
            {
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
                context = null;
            }
        }
    }
}
