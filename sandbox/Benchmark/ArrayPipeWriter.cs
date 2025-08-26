using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;

namespace Benchmark;

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