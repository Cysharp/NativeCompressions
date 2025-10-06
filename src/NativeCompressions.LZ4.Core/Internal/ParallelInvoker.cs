using System.Threading.Tasks.Sources;

namespace NativeCompressions.Internal;

internal class ParallelInvoker : IThreadPoolWorkItem, IValueTaskSource
{
    readonly Func<int, CancellationToken, Task> body;
    readonly CancellationTokenSource cancellationTokenSource;
    int workerId = -1;
    int remaining;

    ManualResetValueTaskSourceCore<object?> core;

    ParallelInvoker(int maxDegreeOfParallelism, CancellationToken cancellationToken, Func<int, CancellationToken, Task> body)
    {
        this.body = body;
        this.cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.remaining = maxDegreeOfParallelism;
    }

    public static ValueTask InvokeAsync(int maxDegreeOfParallelism, CancellationToken cancellationToken, Func<int, CancellationToken, Task> body)
    {
        if (maxDegreeOfParallelism <= 0) maxDegreeOfParallelism = 1;

        var worker = new ParallelInvoker(maxDegreeOfParallelism, cancellationToken, body);
        for (int i = 0; i < maxDegreeOfParallelism; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(worker, preferLocal: false);
        }
        return new ValueTask(worker, worker.core.Version);
    }

    async Task TaskBody()
    {
        try
        {
            var id = Interlocked.Increment(ref workerId);
            await body(id, cancellationTokenSource.Token);

            if (Interlocked.Decrement(ref remaining) == 0)
            {
                cancellationTokenSource.Dispose();
                core.SetResult(null); // all worker completed.
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref remaining, -1) > 0) // call error on first
            {
                cancellationTokenSource.Cancel(); // if one worker failed, other workers should stop as soon as possible.
                cancellationTokenSource.Dispose();
                core.SetException(ex);
            }
        }
    }

    void IThreadPoolWorkItem.Execute()
    {
        _ = TaskBody(); // start run on threadpool.
    }

    void IValueTaskSource.GetResult(short token)
    {
        core.GetResult(token);
    }

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
    {
        return core.GetStatus(token);
    }

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        core.OnCompleted(continuation, state, token, flags);
    }
}
