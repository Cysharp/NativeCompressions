using NativeCompressions.Internal;
using System.Buffers;
using NativeCompressions.Interop;
using static NativeCompressions.Interop.ZstandardNativeMethods;
using System.IO.Compression;

namespace NativeCompressions;

public static partial class Zstandard
{
    public static byte[] Decompress(ReadOnlySpan<byte> source, bool trustedData = false)
    {
        return Decompress(source, ZstandardDecompressionOptions.Default, trustedData);
    }

    public static unsafe byte[] Decompress(ReadOnlySpan<byte> source, in ZstandardDecompressionOptions decompressionOptions, bool trustedData = false)
    {
        // TODO: is this ok to trust frame header on multithread-data(multi-frame?)
        if (trustedData && TryGetFrameContentSize(source, out var size))
        {
            if (size > (ulong)Array.MaxLength)
            {
                throw new ZstandardException($"Frame size {size} exceeds maximum array size");
            }

            var destination = GC.AllocateUninitializedArray<byte>((int)size);

            var bytesWritten = Decompress(source, destination, decompressionOptions);

            if (bytesWritten != destination.Length)
            {
                throw new ZstandardException($"Decompressed size mismatch. Expected {destination.Length}, got {bytesWritten}");
            }

            return destination;
        }
        else
        {
            using var decoder = new ZstandardDecoder(decompressionOptions);

            Span<byte> scratch = stackalloc byte[256];
            var arrayProvider = new SegmentedArrayProvider<byte>(scratch);
            var dest = arrayProvider.GetSpan();

            var status = OperationStatus.DestinationTooSmall;
            while (status == OperationStatus.DestinationTooSmall)
            {
                status = decoder.Decompress(source, dest, out var bytesConsumed, out var bytesWritten);

                source = source.Slice(bytesConsumed);
                dest = dest.Slice(bytesWritten);
                arrayProvider.Advance(bytesWritten);

                if (dest.Length == 0)
                {
                    dest = arrayProvider.GetSpan();
                }
            }

            if (status != OperationStatus.Done)
            {
                throw new ZstandardException($"Decompression failed: {status}");
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

    // TODO: DecompressAsync variations
}
