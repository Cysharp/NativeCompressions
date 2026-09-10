namespace NativeCompressions.Internal;

// PriorityQueue<,> is not available in netstandard2.1, and the queues here are tiny (bounded by the thread count),
// so a sorted array is enough. Stored in reverse order so the smallest item is at the end and cheap to remove.
internal struct MiniPriorityQueue<T>
    where T : IComparable<T>
{
    static readonly Comparer<T> ReverseComparer = Comparer<T>.Create((x, y) => y.CompareTo(x));

    T[] items;
    int count;

    public int Count => count;

    public ReadOnlySpan<T> Values => items.AsSpan(0, count);

    public MiniPriorityQueue()
    {
        items = new T[8];
        count = 0;
    }

    public ref T Peek()
    {
        if (count == 0) throw new InvalidOperationException("The queue is empty.");
        return ref items[count - 1];
    }

    public void Enqueue(T item)
    {
        if (count == items.Length)
        {
            Array.Resize(ref items, items.Length * 2);
        }

        var index = Array.BinarySearch(items, 0, count, item, ReverseComparer);
        if (index < 0) index = ~index;

        if (index < count)
        {
            Array.Copy(items, index, items, index + 1, count - index);
        }
        items[index] = item;
        count++;
    }

    public T Dequeue()
    {
        if (count == 0) throw new InvalidOperationException("The queue is empty.");
        var item = items[count - 1];
        items[count - 1] = default!;
        count--;
        return item;
    }
}
