using System.Buffers;
using System.IO.Pipelines;

namespace NativeCompressions;

public static partial class Zstandard
{
    public static async ValueTask DecompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZstandardDecompressionOptions? options = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(source, destination, decoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask DecompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, ZstandardDecoder decoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        var status = OperationStatus.DestinationTooSmall;
        while (status == OperationStatus.DestinationTooSmall)
        {
            var dest = destination.GetSpan(requestBufferSize);

            status = decoder.Decompress(source.Span, dest, out var bytesConsumed, out var bytesWritten);
            source = source.Slice(bytesConsumed);

            destination.Advance(bytesWritten);
            await destination.FlushAsync(cancellationToken);
        }

        if (status != OperationStatus.Done)
        {
            throw new ZstandardException($"Zstandard decoder returns {status}.");
        }
    }

    public static async ValueTask DecompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, ZstandardDecompressionOptions? options = null, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        using var decoder = new ZstandardDecoder(options ?? ZstandardDecompressionOptions.Default);
        await DecompressAsync(source, destination, decoder, requestBufferSize, cancellationToken);
    }

    public static async ValueTask DecompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, ZstandardDecoder decoder, int requestBufferSize = MinimumBufferSize, CancellationToken cancellationToken = default)
    {
        var status = OperationStatus.DestinationTooSmall;
        var dest = destination.GetSpan(requestBufferSize);
        var writtenInDest = 0;

        // consume all sources
        foreach (var item in source)
        {
            var chunk = item;
            while (chunk.Length > 0) // keep loop if DestinationTooSmall or NeedMoreData
            {
                status = decoder.Decompress(chunk.Span, dest, out var bytesConsumed, out var bytesWritten);
                chunk = chunk.Slice(bytesConsumed);
                dest = dest.Slice(bytesWritten);
                writtenInDest += bytesWritten;

                if (status == OperationStatus.Done)
                {
                    goto END;
                }
                else if (status == OperationStatus.InvalidData)
                {
                    writtenInDest = 0; // no need to flush
                    goto END;
                }

                if (dest.Length == 0)
                {
                    destination.Advance(writtenInDest);
                    await destination.FlushAsync(cancellationToken);
                    dest = destination.GetSpan(requestBufferSize);
                    writtenInDest = 0;
                }
            }
        }

    END:
        if (writtenInDest > 0)
        {
            destination.Advance(writtenInDest);
            await destination.FlushAsync(cancellationToken);
        }

        // flush remaining data in native context
        while (status == OperationStatus.DestinationTooSmall)
        {
            dest = destination.GetSpan(requestBufferSize);
            status = decoder.Decompress([], dest, out _, out var bytesWritten);
            destination.Advance(bytesWritten);
            await destination.FlushAsync(cancellationToken);
        }

        if (status != OperationStatus.Done)
        {
            throw new ZstandardException($"Zstandard decoder returns {status}.");
        }
    }

    // TODO: more overloads
}
