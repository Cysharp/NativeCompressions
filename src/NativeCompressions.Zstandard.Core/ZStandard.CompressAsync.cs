using System.Buffers;
using System.IO.Pipelines;

namespace NativeCompressions;

public static partial class Zstandard
{
    const int AllowParallelCompressThreshold = 1024 * 1024; // 1MB
    const int MinimumBufferSize = 65536;

    public static ValueTask CompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZstandardCompressionOptions? options = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        var compressionOptions = options ?? ZstandardCompressionOptions.Default;
        if (!(maxDegreeOfParallelism is null or 1) && source.Length > AllowParallelCompressThreshold)
        {
            // multi-thread
            compressionOptions = compressionOptions with { NbWorkers = maxDegreeOfParallelism.Value };
        }

        using var encoder = new ZstandardEncoder(compressionOptions);
        return CompressAsync(source, destination, encoder, cancellationToken);
    }

    public static async ValueTask CompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZstandardEncoder encoder, CancellationToken cancellationToken = default)
    {
        var maxCompressedLength = Zstandard.GetMaxCompressedLength(source.Length);
        var sizeHint = Math.Min(maxCompressedLength, MinimumBufferSize);

        var status = OperationStatus.DestinationTooSmall;
        while (status != OperationStatus.Done)
        {
            var dest = destination.GetSpan(sizeHint);

            status = encoder.Compress(source.Span, dest, out var bytesConsumed, out var bytesWritten, isFinalBlock: true);
            source = source.Slice(bytesConsumed);

            if (status == OperationStatus.InvalidData)
            {
                throw new ZstandardException("ZStandardEncoder returns InvalidData.");
            }

            destination.Advance(bytesWritten);
            await destination.FlushAsync(cancellationToken);
        }
    }
}
