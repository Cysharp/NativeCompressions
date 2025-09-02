#if !NET8_0_OR_GREATER

using System.Diagnostics;

namespace System.Threading.Tasks
{
    internal static class TaskToAsyncResult
    {
        public static IAsyncResult Begin(Task task, AsyncCallback? callback, object? state)
        {
            if (task is null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            return new TaskAsyncResult(task, state, callback);
        }

        public static void End(IAsyncResult asyncResult) => Unwrap(asyncResult).GetAwaiter().GetResult();

        public static TResult End<TResult>(IAsyncResult asyncResult) => Unwrap<TResult>(asyncResult).GetAwaiter().GetResult();

        public static Task Unwrap(IAsyncResult asyncResult)
        {
            if (asyncResult is null)
            {
                throw new ArgumentNullException(nameof(asyncResult));
            }

            if ((asyncResult as TaskAsyncResult)?.task is not Task task)
            {
                throw new ArgumentException(null, nameof(asyncResult));
            }

            return task;
        }

        public static Task<TResult> Unwrap<TResult>(IAsyncResult asyncResult)
        {
            if (asyncResult is null)
            {
                throw new ArgumentNullException(nameof(asyncResult));
            }

            if ((asyncResult as TaskAsyncResult)?.task is not Task<TResult> task)
            {
                throw new ArgumentException(null, nameof(asyncResult));
            }

            return task;
        }

        private sealed class TaskAsyncResult : IAsyncResult
        {
            internal readonly Task task;
            private readonly AsyncCallback? callback;

            public object? AsyncState { get; }
            public bool CompletedSynchronously { get; }
            public bool IsCompleted => task.IsCompleted;
            public WaitHandle AsyncWaitHandle => ((IAsyncResult)task).AsyncWaitHandle;

            internal TaskAsyncResult(Task task, object? state, AsyncCallback? callback)
            {
                Debug.Assert(task is not null);

                this.task = task;
                AsyncState = state;

                if (task.IsCompleted)
                {
                    CompletedSynchronously = true;
                    callback?.Invoke(this);
                }
                else if (callback is not null)
                {
                    this.callback = callback;
                    this.task.ConfigureAwait(continueOnCapturedContext: false)
                         .GetAwaiter()
                         .OnCompleted(() => this.callback.Invoke(this));
                }
            }
        }
    }
}

#endif