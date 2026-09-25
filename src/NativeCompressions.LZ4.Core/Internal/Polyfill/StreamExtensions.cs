#if NETSTANDARD2_0

using System.Buffers;
using System.Runtime.InteropServices;

namespace NativeCompressions.Internal
{
    // netstandard2.0 Stream has no Span / Memory overloads and no DisposeAsync.
    // Array backed memory is passed through, anything else goes through a pooled copy.
    internal static class StreamExtensions
    {
        public static ValueTask WriteAsync(this Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (MemoryMarshal.TryGetArray(buffer, out var segment))
            {
                return new ValueTask(stream.WriteAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken));
            }
            return WriteCopyAsync(stream, buffer, cancellationToken);
        }

        static async ValueTask WriteCopyAsync(Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            var array = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.Span.CopyTo(array);
                await stream.WriteAsync(array, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(array);
            }
        }

        public static ValueTask<int> ReadAsync(this Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (MemoryMarshal.TryGetArray<byte>(buffer, out var segment))
            {
                return new ValueTask<int>(stream.ReadAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken));
            }
            return ReadCopyAsync(stream, buffer, cancellationToken);
        }

        static async ValueTask<int> ReadCopyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var array = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                var read = await stream.ReadAsync(array, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                array.AsSpan(0, read).CopyTo(buffer.Span);
                return read;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(array);
            }
        }

        public static ValueTask DisposeAsync(this Stream stream)
        {
            if (stream is IAsyncDisposable asyncDisposable)
            {
                return asyncDisposable.DisposeAsync();
            }
            stream.Dispose();
            return default;
        }
    }
}

#endif
