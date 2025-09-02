using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

#if NETSTANDARD2_0 || NETSTANDARD2_1
#pragma warning disable

namespace System.Runtime.InteropServices
{
    internal static class CollectionsMarshal
    {
        internal static readonly int ListSize;

        static CollectionsMarshal()
        {
            try
            {
                ListSize = typeof(List<>).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Length;
            }
            catch
            {
                ListSize = 3;
            }
        }

        internal static Span<T> AsSpan<T>(this List<T>? list)
        {
            Span<T> span = default;
            if (list is not null)
            {
                if (ListSize == 3)
                {
                    var view = Unsafe.As<ListViewA<T>>(list);
                    T[] items = view._items;
                    span = items.AsSpan(0, list.Count);
                }
                else if (ListSize == 4)
                {
                    var view = Unsafe.As<ListViewB<T>>(list);
                    T[] items = view._items;
                    span = items.AsSpan(0, list.Count);
                }
            }

            return span;
        }
    }


    internal class ListViewA<T>
    {
        public T[] _items;
        public int _size;
        public int _version;
    }

    internal class ListViewB<T>
    {
        public T[] _items;
        public int _size;
        public int _version;
        private Object _syncRoot; // in .NET Framework
    }
}

#endif
