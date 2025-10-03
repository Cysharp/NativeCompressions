using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;

namespace Benchmark;

public class ArrayPipeWriter : PipeWriter
{
    readonly byte[] array;
    int position;
    bool completed;

    public ArrayPipeWriter(byte[] array)
    {
        this.array = array;
    }

    public override void Advance(int bytes)
    {
        position += bytes;
    }

    public override void CancelPendingFlush()
    {
    }

    public override void Complete(Exception? exception = null)
    {
        if (exception != null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
        completed = true;
    }

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        return new(new FlushResult(cancellationToken.IsCancellationRequested, completed));
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        var memory = array.AsMemory(position);
        if (memory.Length < sizeHint)
        {
            ThrowInvalid();
        }
        return memory;
    }

    public override Span<byte> GetSpan(int sizeHint = 0)
    {
        var span = array.AsSpan(position);
        if (span.Length < sizeHint)
        {
            ThrowInvalid();
        }
        return span;
    }

    public void Clear() => position = 0;
    public void ResetWrittenCount() => position = 0;
    public ReadOnlySpan<byte> WrittenSpan => array.AsSpan(0, position);
    public ReadOnlyMemory<byte> WrittenMemory => array.AsMemory(0, position);
    public int WrittenCount => position;

    void ThrowInvalid() => throw new InvalidOperationException("Not enough space in the array.");
}

public class ArrayBufferPipeWriter : PipeWriter
{
    readonly ArrayBufferWriter<byte> writer;
    bool completed;

    public ArrayBufferPipeWriter()
    {
        writer = new ArrayBufferWriter<byte>();
    }

    public ArrayBufferPipeWriter(int initialCapacity)
    {
        writer = new ArrayBufferWriter<byte>(initialCapacity);
    }

    public override void Advance(int bytes)
    {
        writer.Advance(bytes);
    }

    public override void CancelPendingFlush()
    {
    }

    public override void Complete(Exception? exception = null)
    {
        if (exception != null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
        completed = true;
    }

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        return new(new FlushResult(cancellationToken.IsCancellationRequested, completed));
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        return writer.GetMemory(sizeHint);
    }

    public override Span<byte> GetSpan(int sizeHint = 0)
    {
        return writer.GetSpan(sizeHint);
    }

    public void Clear() => writer.Clear();
    public void ResetWrittenCount() => writer.ResetWrittenCount();
    public ReadOnlySpan<byte> WrittenSpan => writer.WrittenSpan;
    public ReadOnlyMemory<byte> WrittenMemory => writer.WrittenMemory;
    public int WrittenCount => writer.WrittenCount;
}
