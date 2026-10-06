using System.IO.Pipelines;

namespace NativeCompressions.Internal;

// FlushAsync and WriteAsync report a cancelled flush and a completed reader through their result, not by throwing.
// Ignoring the result would report success for output that was never delivered.
// A completed reader is an I/O failure of the destination, not a cancellation, so a caller that
// catches OperationCanceledException to ignore its own token does not swallow it.
internal static class PipeWriterExtensions
{
    public static async ValueTask FlushAndCheckAsync(this PipeWriter writer, CancellationToken cancellationToken)
    {
        var result = await writer.FlushAsync(cancellationToken);
        ThrowIfStopped(result);
    }

    public static async ValueTask WriteAndCheckAsync(this PipeWriter writer, ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        var result = await writer.WriteAsync(source, cancellationToken);
        ThrowIfStopped(result);
    }

    static void ThrowIfStopped(FlushResult result)
    {
        if (result.IsCanceled)
        {
            throw new OperationCanceledException("The flush of the destination was canceled.");
        }

        if (result.IsCompleted)
        {
            // nobody reads the rest, stop instead of processing the remaining input
            throw new IOException("The reader of the destination has completed.");
        }
    }
}
