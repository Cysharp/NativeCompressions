using NativeCompressions.Internal;
using NativeCompressions.Interop;
using static NativeCompressions.Interop.LZ4NativeMethods;
using System.Buffers;
using System.IO.Compression;

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

        using var decoder = new LZ4Decoder(options.WithoutStableDst());

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
        try
        {
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
        finally
        {
            arrayProvider.Clear(); // invalid data throws in the middle, the rented segments go back either way
        }
    }

    /// <summary>
    /// Decompresses one or more concatenated frames into the destination buffer and returns the number of bytes written.
    /// </summary>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination) => Decompress(source, destination, LZ4DecompressionOptions.Default);

    /// <summary>
    /// Decompresses one or more concatenated frames into the destination buffer and returns the number of bytes written.
    /// </summary>
    /// <exception cref="LZ4Exception">Thrown when the data is invalid, ends inside a frame, or does not fit in the destination.</exception>
    public static unsafe int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, in LZ4DecompressionOptions options)
    {
        if (source.IsEmpty)
        {
            return 0;
        }

        // lz4frame is called directly instead of through LZ4Decoder. All of the input is here, so anything
        // short of a finished frame is an error, and the call allocates nothing managed.
        var decompressOptions = options.WithoutStableDst().ToDecompressOptions();

        LZ4F_dctx_s* context = null;
        LZ4Dictionary.Lease dictionary = default;
        try
        {
            ThrowIfError(LZ4F_createDecompressionContext(&context, FrameVersion));
            dictionary = options.AcquireDictionary();

            fixed (byte* src = source)
            fixed (byte* dest = destination)
            {
                var totalConsumed = 0;
                var totalWritten = 0;
                while (true)
                {
                    var consumed = (nuint)(source.Length - totalConsumed);
                    var written = (nuint)(destination.Length - totalWritten);

                    // returns a hint of the next source size, 0 when the frame is complete, or an error code
                    var hintOrErrorCode = dictionary.IsEmpty
                        ? LZ4F_decompress(context, dest + totalWritten, &written, src + totalConsumed, &consumed, &decompressOptions)
                        : LZ4F_decompress_usingDict(context, dest + totalWritten, &written, src + totalConsumed, &consumed, dictionary.Data, (nuint)dictionary.DataLength, &decompressOptions);
                    ThrowIfError(hintOrErrorCode);

                    totalConsumed += (int)consumed;
                    totalWritten += (int)written;

                    if (hintOrErrorCode == 0)
                    {
                        // The frame is complete. Another one may follow, lz4frame reads it without a reset.
                        if (totalConsumed == source.Length) return totalWritten;
                        if (consumed == 0) throw new LZ4Exception("Invalid LZ4 frame: decoder made no progress.");
                        continue;
                    }

                    if (totalWritten == destination.Length) throw new LZ4Exception("Destination buffer is too small.");
                    if (totalConsumed == source.Length) throw new LZ4Exception("Invalid LZ4 frame: input ends inside a frame.");
                    if (consumed == 0 && written == 0) throw new LZ4Exception("Invalid LZ4 frame: decoder made no progress.");
                }
            }
        }
        finally
        {
            LZ4F_freeDecompressionContext(context); // accepts null
            dictionary.Dispose();
        }
    }
}
