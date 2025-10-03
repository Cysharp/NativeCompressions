#if NETSTANDARD2_1

using Microsoft.Win32.SafeHandles;

namespace NativeCompressions.Internal
{
    internal static class StaticExtensions
    {
        extension(Array)
        {
            public static int MaxLength => 0X7FFFFFC7;
        }

        extension(GC)
        {
            public static T[] AllocateUninitializedArray<T>(int length) => new T[length];
        }

        extension(ValueTask)
        {
            public static ValueTask FromCanceled(CancellationToken cancellationToken) => new ValueTask(Task.FromCanceled(cancellationToken));
        }

        extension(File)
        {
            public static SafeFileHandle OpenHandle(string path, FileMode mode = FileMode.Open, FileAccess access = FileAccess.Read, FileShare share = FileShare.Read, FileOptions options = FileOptions.None, long preallocationSize = 0)
            {
                var fs = new FileStream(path, mode, access, share, preallocationSize == 0 ? 1 : (int)preallocationSize, options);
                return fs.SafeFileHandle;
            }
        }

        extension(ThreadPool)
        {
            public static void UnsafeQueueUserWorkItem(IThreadPoolWorkItem workItem, bool preferLocal)
            {
                ThreadPool.QueueUserWorkItem(_ => workItem.Execute());
            }
        }
    }
}

namespace System.Threading
{
    internal interface IThreadPoolWorkItem
    {
        void Execute();
    }
}

#endif
