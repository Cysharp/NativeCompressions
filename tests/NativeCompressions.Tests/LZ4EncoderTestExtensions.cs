using System.Buffers;

namespace NativeCompressions.Tests;

// Most tests give the encoder all the room it needs, so every call has to finish at once.
// These assert that and return the number of bytes written.
static class LZ4EncoderTestExtensions
{
    public static int CompressDone(this LZ4Encoder encoder, ReadOnlySpan<byte> source, Span<byte> destination, bool isFinalBlock = false)
    {
        Assert.Equal(OperationStatus.Done, encoder.Compress(source, destination, out var consumed, out var written, isFinalBlock));
        Assert.Equal(source.Length, consumed);
        return written;
    }

    public static int FlushDone(this LZ4Encoder encoder, Span<byte> destination)
    {
        Assert.Equal(OperationStatus.Done, encoder.Flush(destination, out var written));
        return written;
    }

    public static int CloseDone(this LZ4Encoder encoder, Span<byte> destination)
    {
        Assert.Equal(OperationStatus.Done, encoder.Close(destination, out var written));
        return written;
    }
}
