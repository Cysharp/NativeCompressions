#if NETSTANDARD

using System.Buffers;
using System.Runtime.CompilerServices;

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

#if NETSTANDARD2_0
        extension(RuntimeHelpers)
        {
            // conservative, primitives are the only types known not to hold references
            public static bool IsReferenceOrContainsReferences<T>() => !typeof(T).IsPrimitive;
        }

        extension<T>(ReadOnlySequence<T> sequence)
        {
            public ReadOnlySpan<T> FirstSpan => sequence.First.Span;
        }
#endif
    }
}

#endif
