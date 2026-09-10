using NativeCompressions.Internal;
using System.Buffers;
using NativeCompressions.Interop;
using static NativeCompressions.Interop.ZstandardNativeMethods;
using System.IO.Compression;

namespace NativeCompressions;

public static partial class Zstandard
{
    /// <summary>
    /// Decompresses one or more concatenated frames into a new array.
    /// </summary>
    /// <param name="source">Compressed data. Empty input returns an empty array.</param>
    /// <param name="trustedData">
    /// When true, the result array is allocated up front from the sizes recorded in the frame headers and the
    /// whole input is decoded in one call. Only use this for data you control, since the headers are not verified
    /// before the allocation. When false, the data is decoded in blocks into a growing buffer instead. Every frame
    /// in the input is decoded either way.
    /// </param>
    public static byte[] Decompress(ReadOnlySpan<byte> source, bool trustedData = false)
    {
        return Decompress(source, ZstandardDecompressionOptions.Default, trustedData);
    }

    /// <summary>
    /// Decompresses one or more concatenated frames into a new array with specified options.
    /// </summary>
    /// <param name="source">Compressed data. Empty input returns an empty array.</param>
    /// <param name="decompressionOptions">Decompression options such as a dictionary.</param>
    /// <param name="trustedData">
    /// When true, the result array is allocated up front from the sizes recorded in the frame headers and the
    /// whole input is decoded in one call. Only use this for data you control, since the headers are not verified
    /// before the allocation. When false, the data is decoded in blocks into a growing buffer instead. Every frame
    /// in the input is decoded either way.
    /// </param>
    public static unsafe byte[] Decompress(ReadOnlySpan<byte> source, in ZstandardDecompressionOptions decompressionOptions, bool trustedData = false)
    {
        if (source.IsEmpty)
        {
            return [];
        }

        // The bound covers every frame in the input. It is exact when each frame records its content size.
        // A frame without one contributes blocks * block size max, so the bound can exceed the real size.
        // Truncated or corrupted input has no bound and goes through the streaming path, which reports the error.
        if (trustedData && TryGetMaxDecompressedLength(source, out var bound) && bound <= Array.MaxLength)
        {
            var destination = GC.AllocateUninitializedArray<byte>((int)bound);

            // zstd itself rejects a frame whose decoded size differs from the recorded content size,
            // so a short result only means some frame had no recorded size.
            var bytesWritten = Decompress(source, destination, decompressionOptions);
            if (bytesWritten == destination.Length)
            {
                return destination;
            }

            var result = GC.AllocateUninitializedArray<byte>(bytesWritten);
            destination.AsSpan(0, bytesWritten).CopyTo(result);
            return result;
        }
        else
        {
            using var decoder = new ZstandardDecoder(decompressionOptions);

            Span<byte> scratch = stackalloc byte[256];
            var arrayProvider = new SegmentedArrayProvider<byte>(scratch);
            var dest = arrayProvider.GetSpan();

            // Same behavior as ZstandardStream and DecompressAsync: every frame is decoded,
            // input that ends inside a frame or trailing garbage is an error.
            while (true)
            {
                var status = decoder.Decompress(source, dest, out var bytesConsumed, out var bytesWritten);

                source = source.Slice(bytesConsumed);
                dest = dest.Slice(bytesWritten);
                arrayProvider.Advance(bytesWritten);

                if (status == OperationStatus.Done)
                {
                    if (source.IsEmpty) break;
                    decoder.Reset(); // another frame follows
                }
                else if (status == OperationStatus.NeedMoreData && source.IsEmpty)
                {
                    throw new ZstandardException("Decompression failed: input ends inside a frame.");
                }
                else if (status == OperationStatus.InvalidData)
                {
                    throw new ZstandardException("Decompression failed: invalid data.");
                }

                if (dest.Length == 0)
                {
                    dest = arrayProvider.GetSpan();
                }
            }

            var result = GC.AllocateUninitializedArray<byte>(arrayProvider.Count);
            arrayProvider.CopyToAndClear(result);
            return result;
        }
    }

    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        return Decompress(source, destination, ZstandardDecompressionOptions.Default);
    }

    public static unsafe int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, in ZstandardDecompressionOptions decompressionOptions)
    {
        // Currently DecompressionOptions.WindowLogMax in only used in streaming mode.
        // So always use simple API when default options are used.

        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            nuint bytesWritten;
            if (decompressionOptions.Dictionary == null)
            {
                bytesWritten = ZSTD_decompress(dest, (nuint)destination.Length, src, (nuint)source.Length);
            }
            else
            {
                var context = ZSTD_createDCtx();
                if (context == null) throw new ZstandardException("Failed to create decompression context");

                try
                {
                    bytesWritten = ZSTD_decompress_usingDDict(context, dest, (nuint)destination.Length, src, (nuint)source.Length, decompressionOptions.Dictionary.DecompressionHandle);
                }
                finally
                {
                    ZSTD_freeDCtx(context);
                }
            }
            ThrowIfError(bytesWritten);

            return (int)bytesWritten;
        }
    }
}
