using NativeCompressions.Internal;
using System.Buffers;
using System.Text;
using static NativeCompressions.ZStandard.Raw.NativeMethods;

namespace NativeCompressions.ZStandard;

public static partial class ZStandard
{
    /// <summary>
    /// Decompresses ZStandard compressed data.
    /// </summary>
    public static unsafe byte[] Decompress(ReadOnlySpan<byte> source, ZStandardCompressionDictionary? dictionary = null, bool trustedData = false)
    {
        // TODO: is this ok to trust frame header on multithread-data(multi-frame?)
        if (trustedData && TryGetFrameContentSize(source, out var size))
        {
            const int ArrayMaxLength = 0X7FFFFFC7;

            if (size > ArrayMaxLength)
            {
                throw new InvalidOperationException($"Frame size {size} exceeds maximum array size");
            }

#if NETSTANDARD2_1
            var destination = new byte[size];
#else
            var destination = GC.AllocateUninitializedArray<byte>((int)size);
#endif

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

                if ((int)bytesWritten != destination.Length)
                {
                    throw new ZStandardException($"Decompressed size mismatch. Expected {destination.Length}, got {bytesWritten}");
                }

                return destination;
            }
        }
        else
        {
            using var decoder = new ZStandardDecoder(ZStandardDecompressionOptions.Default, dictionary); // TODO: option?

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

#if NETSTANDARD2_1
            var result = new byte[arrayProvider.Count];
#else
            var result = GC.AllocateUninitializedArray<byte>(arrayProvider.Count);
#endif
            arrayProvider.CopyToAndClear(result);
            return result;
        }
    }
}
