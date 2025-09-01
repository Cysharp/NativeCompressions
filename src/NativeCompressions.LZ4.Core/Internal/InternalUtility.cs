using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NativeCompressions.LZ4
{
    internal static class InternalUtility
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] FastToArray(this byte[] source, int length)
        {
            var dest = GC.AllocateUninitializedArray<byte>(length);
            unsafe
            {
                fixed (void* s = source)
                fixed (void* d = dest)
                {
                    Buffer.MemoryCopy(s, d, length, length);
                }
            }
            return dest;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] FastToArray(this Span<byte> source, int length)
        {
            var dest = GC.AllocateUninitializedArray<byte>(length);
            unsafe
            {
                fixed (void* s = &MemoryMarshal.GetReference(source))
                fixed (void* d = dest)
                {
                    Buffer.MemoryCopy(s, d, length, length);
                }
            }
            return dest;
        }
    }
}
