using NativeCompressions.LZ4.Internal;
using System.Buffers;
using System.Net.NetworkInformation;

namespace NativeCompressions.LZ4;

public static partial class LZ4
{
    public static unsafe byte[] Decompress(ReadOnlySpan<byte> source, bool trustedData = false)
    {
        using var decoder = new LZ4Decoder();

        if (trustedData)
        {
            var frameInfo = decoder.GetFrameInfo(source, out var consumed);
            source = source.Slice(consumed);

            if (frameInfo.ContentSize != 0) // 0 means unknown
            {
                var destination = new byte[frameInfo.ContentSize]; // trusted ContentSize, decode one-shot.
                var dest = destination.AsSpan();
                var status = OperationStatus.DestinationTooSmall;
                while (status == OperationStatus.DestinationTooSmall && source.Length > 0)
                {
                    status = decoder.Decompress(source, dest, out var bytesConsumed, out var bytesWritten);
                    source = source.Slice(bytesConsumed);
                    dest = dest.Slice(bytesWritten);
                }

                if (status == OperationStatus.NeedMoreData)
                {
                    throw new InvalidOperationException("Invalid LZ4 frame.");
                }

                return destination;
            }
        }

        {
            Span<byte> scratch = stackalloc byte[127];
            var arrayProvider = new SegmentedArrayProvider<byte>(scratch);

            var dest = arrayProvider.GetSpan();
            var status = OperationStatus.DestinationTooSmall;
            while (status == OperationStatus.DestinationTooSmall && source.Length > 0)
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

            if (status == OperationStatus.NeedMoreData)
            {
                throw new InvalidOperationException("Invalid LZ4 frame.");
            }

            var result = GC.AllocateUninitializedArray<byte>(arrayProvider.Count);
            arrayProvider.CopyToAndClear(result);
            return result;
        }
    }
}
