using NativeCompressions.Internal;
using System.Buffers;
using static NativeCompressions.ZStandard.Raw.NativeMethods;

namespace NativeCompressions.ZStandard;

public static partial class ZStandard
{
    public static byte[] Decompress(ReadOnlySpan<byte> source, ZStandardCompressionDictionary? dictionary = null, bool trustedData = false)
    {
        return Decompress(source, ZStandardDecompressionOptions.Default, dictionary, trustedData);
    }

    public static unsafe byte[] Decompress(ReadOnlySpan<byte> source, in ZStandardDecompressionOptions decompressionOptions, ZStandardCompressionDictionary? dictionary = null, bool trustedData = false)
    {
        // TODO: is this ok to trust frame header on multithread-data(multi-frame?)
        if (trustedData && decompressionOptions.IsDefault && TryGetFrameContentSize(source, out var size))
        {
            if (size > (ulong)Array.MaxLength)
            {
                throw new ZStandardException($"Frame size {size} exceeds maximum array size");
            }

            var destination = GC.AllocateUninitializedArray<byte>((int)size);

            var bytesWritten = Decompress(source, destination, decompressionOptions, dictionary);

            if (bytesWritten != destination.Length)
            {
                throw new ZStandardException($"Decompressed size mismatch. Expected {destination.Length}, got {bytesWritten}");
            }

            return destination;
        }
        else
        {
            using var decoder = new ZStandardDecoder(decompressionOptions, dictionary);

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
                throw new ZStandardException($"Decompression failed: {status}");
            }

            var result = GC.AllocateUninitializedArray<byte>(arrayProvider.Count);
            arrayProvider.CopyToAndClear(result);
            return result;
        }
    }

    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, ZStandardCompressionDictionary? dictionary = null)
    {
        return Decompress(source, destination, ZStandardDecompressionOptions.Default, dictionary);
    }

    public static unsafe int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, in ZStandardDecompressionOptions decompressionOptions, ZStandardCompressionDictionary? dictionary = null)
    {
        if (decompressionOptions.IsDefault)
        {
            fixed (byte* src = source)
            fixed (byte* dest = destination)
            {
                nuint bytesWritten;
                if (dictionary == null)
                {
                    bytesWritten = ZSTD_decompress(dest, (nuint)destination.Length, src, (nuint)source.Length);
                }
                else
                {
                    var context = ZSTD_createDCtx();
                    if (context == null) throw new ZStandardException("Failed to create decompression context");

                    try
                    {
                        bytesWritten = ZSTD_decompress_usingDDict(context, dest, (nuint)destination.Length, src, (nuint)source.Length, dictionary.DecompressionHandle);
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
        else
        {
            using var decoder = new ZStandardDecoder(decompressionOptions, dictionary);

            var totalWritten = 0;
            var status = OperationStatus.DestinationTooSmall;
            while (status == OperationStatus.DestinationTooSmall && destination.Length != 0)
            {
                status = decoder.Decompress(source, destination, out var bytesConsumed, out var bytesWritten);

                source = source.Slice(bytesConsumed);
                destination = destination.Slice(bytesWritten);
                totalWritten += bytesWritten;
            }

            if (status != OperationStatus.Done)
            {
                throw new ZStandardException($"Decompression failed: {status}");
            }

            return totalWritten;
        }
    }

    // TODO: DecompressAsync variations
}
