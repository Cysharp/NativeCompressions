using System.Runtime.InteropServices;

namespace NativeCompressions.Internal;

// use PriorityQueue<,> is easy to use but not available in netstandard2.1
// this simple implementation should be effective in a small size.
internal struct MiniPriorityQueue<T>
    where T : IComparable<T>
{
    // reverse-order
    static readonly Comparer<T> ReverseComparer = Comparer<T>.Create((x, y) => y.CompareTo(x));

    List<T> list;

    public int Count => list.Count;

    public ReadOnlySpan<T> Values => CollectionsMarshal.AsSpan<T>(list);

    public MiniPriorityQueue()
    {
        list = new List<T>();
    }

    public ref T Peek() => ref CollectionsMarshal.AsSpan(list)[^1];

    public void Enqueue(T item)
    {
        // Binary search for insertion point
        int index = list.BinarySearch(item, ReverseComparer);
        if (index < 0) index = ~index;
        list.Insert(index, item);
    }

    public T Dequeue()
    {
        var item = list[^1];
        list.RemoveAt(list.Count - 1); // Remove from last is performant than remove first
        return item;
    }
}
