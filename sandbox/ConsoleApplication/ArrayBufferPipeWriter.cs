//using K4os.Compression.LZ4.Streams;
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.ExceptionServices;
//var writer1 = new ArrayBufferPipeWriter();

//var path = @"silesia.tar.lz4";
//var src = File.ReadAllBytes(path);

//Console.WriteLine(src.Length); // lz4 to lz4...!

//await LZ4.CompressAsync(src, writer1, maxDegreeOfParallelism: 1);
//var dest = writer1.WrittenSpan.ToArray();


//var writer2 = new ArrayBufferPipeWriter();
//await LZ4.DecompressAsync(dest, writer2, maxDegreeOfParallelism: 1);

//var foo = writer2.WrittenSpan.ToArray();


//Console.WriteLine(src.SequenceEqual(foo));


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