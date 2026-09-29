using System.Threading.Tasks.Sources;

namespace NativeCompressions.Internal;

internal class ParallelInvoker : IThreadPoolWorkItem, IValueTaskSource
{
    readonly Func<int, CancellationToken, Task> body;
    readonly CancellationTokenSource cancellationTokenSource;
    readonly CancellationToken cancellationToken;
    int workerId = -1;
    int remaining;
    Exception? failure;

    ManualResetValueTaskSourceCore<object?> core;

    ParallelInvoker(int maxDegreeOfParallelism, CancellationToken cancellationToken, Func<int, CancellationToken, Task> body)
    {
        this.body = body;
        this.cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.cancellationToken = this.cancellationTokenSource.Token;
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
            await body(id, cancellationToken);
        }
        catch (Exception ex)
        {
            if (Interlocked.CompareExchange(ref failure, ex, null) == null) // only first exception
            {
                try
                {
                    cancellationTokenSource.Cancel(); // if one worker failed, other workers should stop as soon as possible.
                }
                catch
                {
                    // a callback registered on the token failed, the first exception is the one to report
                }
            }
        }

        // Completes only after every worker has ended, also on failure.
        // The caller may release what the workers read from as soon as the operation completes.
        if (Interlocked.Decrement(ref remaining) == 0)
        {
            cancellationTokenSource.Dispose();

            var ex = failure;
            if (ex != null)
            {
                core.SetException(ex);
            }
            else
            {
                core.SetResult(null);
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
