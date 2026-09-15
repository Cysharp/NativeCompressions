using NativeCompressions.Internal;
using System.Buffers;

namespace NativeCompressions;

public static partial class LZ4
{
    /// <summary>
    /// Decompresses one or more concatenated frames into a new array.
    /// </summary>
    /// <param name="source">Compressed data. Empty input returns an empty array.</param>
    /// <param name="trustedData">
    /// When true and the first frame header records its content size, the result array for that frame is allocated
    /// up front from the header. Only use this for data you control, since the header is not verified before the allocation.
    /// Every frame in the input is decoded either way.
    /// </param>
    public static byte[] Decompress(ReadOnlySpan<byte> source, bool trustedData = false) => Decompress(source, LZ4DecompressionOptions.Default, trustedData);

    /// <summary>
    /// Decompresses one or more concatenated frames into a new array with specified options.
    /// </summary>
    /// <param name="source">Compressed data. Empty input returns an empty array.</param>
    /// <param name="options">Decompression options such as a dictionary.</param>
    /// <param name="trustedData">
    /// When true and the first frame header records its content size, the result array for that frame is allocated
    /// up front from the header. Only use this for data you control, since the header is not verified before the allocation.
    /// Every frame in the input is decoded either way.
    /// </param>
    public static byte[] Decompress(ReadOnlySpan<byte> source, in LZ4DecompressionOptions options, bool trustedData = false)
    {
        if (source.IsEmpty)
        {
            return [];
        }

        using var decoder = new LZ4Decoder(options);

        if (trustedData && TryGetFrameInfo(source, out var frameInfo) && frameInfo.FrameType == FrameType.Frame && frameInfo.ContentSize != 0)
        {
            if (frameInfo.ContentSize > (ulong)Array.MaxLength)
            {
                throw new LZ4Exception($"Content size {frameInfo.ContentSize} exceeds maximum array size");
            }

            var destination = new byte[frameInfo.ContentSize];
            var dest = destination.AsSpan();
            var status = OperationStatus.NeedMoreData;
            while (true)
            {
                status = decoder.Decompress(source, dest, out var bytesConsumed, out var bytesWritten);
                source = source.Slice(bytesConsumed);
                dest = dest.Slice(bytesWritten);

                if (status == OperationStatus.Done) break;
                if (status == OperationStatus.InvalidData) throw new LZ4Exception("Invalid LZ4 frame.");
                if (status == OperationStatus.NeedMoreData && source.IsEmpty) throw new LZ4Exception("Invalid LZ4 frame: input ends inside a frame.");
                if (dest.IsEmpty) throw new LZ4Exception("Invalid LZ4 frame: content is larger than the recorded content size.");
            }

            if (dest.Length != 0)
            {
                throw new LZ4Exception($"Decompressed size mismatch. Expected {destination.Length}, got {destination.Length - dest.Length}");
            }

            if (source.IsEmpty)
            {
                return destination;
            }

            // more frames follow, decode them into a growing buffer and append
            decoder.Reset();
            var rest = DecompressToArray(decoder, source);
            var combined = GC.AllocateUninitializedArray<byte>(destination.Length + rest.Length);
            destination.CopyTo(combined, 0);
            rest.CopyTo(combined, destination.Length);
            return combined;
        }

        return DecompressToArray(decoder, source);
    }

    // Same behavior as LZ4Stream and DecompressAsync: every frame is decoded, input that ends inside a frame is an error.
    static byte[] DecompressToArray(LZ4Decoder decoder, ReadOnlySpan<byte> source)
    {
        Span<byte> scratch = stackalloc byte[256];
        var arrayProvider = new SegmentedArrayProvider<byte>(scratch);
        var dest = arrayProvider.GetSpan();

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
                throw new LZ4Exception("Invalid LZ4 frame: input ends inside a frame.");
            }
            else if (status == OperationStatus.InvalidData)
            {
                throw new LZ4Exception("Invalid LZ4 frame.");
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

    /// <summary>
    /// Decompresses one or more concatenated frames into the destination buffer and returns the number of bytes written.
    /// </summary>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination) => Decompress(source, destination, LZ4DecompressionOptions.Default);

    /// <summary>
    /// Decompresses one or more concatenated frames into the destination buffer and returns the number of bytes written.
    /// </summary>
    /// <exception cref="LZ4Exception">Thrown when the data is invalid, ends inside a frame, or does not fit in the destination.</exception>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, in LZ4DecompressionOptions options)
    {
        if (source.IsEmpty)
        {
            return 0;
        }

        using var decoder = new LZ4Decoder(options);

        var totalWritten = 0;
        while (true)
        {
            var status = decoder.Decompress(source, destination, out var bytesConsumed, out var bytesWritten);
            source = source.Slice(bytesConsumed);
            destination = destination.Slice(bytesWritten);
            totalWritten += bytesWritten;

            if (status == OperationStatus.Done)
            {
                if (source.IsEmpty) break;
                decoder.Reset(); // another frame follows
            }
            else if (status == OperationStatus.NeedMoreData && source.IsEmpty)
            {
                throw new LZ4Exception("Invalid LZ4 frame: input ends inside a frame.");
            }
            else if (status == OperationStatus.InvalidData)
            {
                throw new LZ4Exception("Invalid LZ4 frame.");
            }
            else if (status == OperationStatus.DestinationTooSmall && destination.IsEmpty)
            {
                throw new LZ4Exception("Destination buffer is too small.");
            }
        }

        return totalWritten;
    }
}
