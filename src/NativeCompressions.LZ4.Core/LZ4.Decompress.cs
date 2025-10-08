using NativeCompressions.Internal;
using System.Buffers;
using System.Runtime.InteropServices;

namespace NativeCompressions;

public static partial class LZ4
{
    public static unsafe byte[] Decompress(ReadOnlySpan<byte> source, bool trustedData = false) => Decompress(source, LZ4DecompressionOptions.Default, trustedData);

    public static unsafe byte[] Decompress(ReadOnlySpan<byte> source, in LZ4DecompressionOptions options, bool trustedData = false)
    {
        using var decoder = new LZ4Decoder(options);

        if (trustedData)
        {
            var frameInfo = decoder.GetFrameInfo(source, out var consumed);
            source = source.Slice(consumed);

            if (frameInfo.ContentSize != 0) // 0 means unknown
            {
                if (frameInfo.ContentSize > (ulong)Array.MaxLength)
                {
                    throw new LZ4Exception($"Content size {frameInfo.ContentSize} exceeds maximum array size");
                }

                var destination = new byte[frameInfo.ContentSize]; // trusted ContentSize, decode one-shot.
                var dest = destination.AsSpan();

                var status = OperationStatus.DestinationTooSmall;
                while (status == OperationStatus.DestinationTooSmall)
                {
                    status = decoder.Decompress(source, dest, out var bytesConsumed, out var bytesWritten);
                    source = source.Slice(bytesConsumed);
                    dest = dest.Slice(bytesWritten);
                }

                if (status != OperationStatus.Done)
                {
                    throw new LZ4Exception("Invalid LZ4 frame.");
                }

                return destination;
            }
        }

        {
            Span<byte> scratch = stackalloc byte[256];
            var arrayProvider = new SegmentedArrayProvider<byte>(scratch);

            var dest = arrayProvider.GetSpan();
            var status = OperationStatus.DestinationTooSmall;
            while (status == OperationStatus.DestinationTooSmall)
            {
                status = decoder.Decompress(source, dest, out var bytesConsumed, out var bytesWritten);
                if (bytesWritten == 0 && bytesConsumed == 0 && status == OperationStatus.DestinationTooSmall)
                {
                    throw new InvalidOperationException("Decoder stuck");
                }

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
                throw new InvalidOperationException("Invalid LZ4 frame.");
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

    public static unsafe int Decompress(ReadOnlySpan<byte> source, Span<byte> destination) => Decompress(source, destination, LZ4DecompressionOptions.Default);

    public static unsafe int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, in LZ4DecompressionOptions options)
    {
        using var decoder = new LZ4Decoder(options);

        var totalWritten = 0;
        var status = OperationStatus.DestinationTooSmall;
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = decoder.Decompress(source, destination, out var bytesConsumed, out var bytesWritten);
            if (bytesWritten == 0 && bytesConsumed == 0 && status == OperationStatus.DestinationTooSmall)
            {
                throw new InvalidOperationException("Decoder stuck");
            }

            source = source.Slice(bytesConsumed);
            destination = destination.Slice(bytesWritten);
            totalWritten += bytesWritten;
        }

        if (status != OperationStatus.Done)
        {
            throw new InvalidOperationException("Invalid LZ4 frame.");
        }

        return totalWritten;
    }
}
