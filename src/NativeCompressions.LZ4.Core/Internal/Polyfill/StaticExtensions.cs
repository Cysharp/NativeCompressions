#if NETSTANDARD2_1

namespace NativeCompressions.Internal;

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
}

#endif
