#if NETSTANDARD
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace NativeCompressions.Internal;

// netstandard has no RandomAccess, so a caller's SafeFileHandle is read through a stream that does not own it.
// The returned stream is never disposed by the library and closing it leaves the handle open.
internal static class NonOwningFileStream
{
    public static Stream Open(SafeFileHandle handle, long offset)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // .NET Framework cannot wrap a handle in a second FileStream: a handle already bound for asynchronous I/O
            // is rejected, and the FileStream left behind by a failed constructor closes the handle when finalized.
            return new Win32ReadStream(handle, offset);
        }

        FileStream stream;
        try
        {
            stream = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: true);
        }
        catch (ArgumentException)
        {
            // the handle was opened without FileOptions.Asynchronous
            stream = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false);
        }

        // FileStream would close the handle when finalized
        GC.SuppressFinalize(stream);

        // always seek, the handle's own file position is unrelated to the requested offset
        stream.Position = offset;
        return stream;
    }

    // Reads at explicit offsets with ReadFile, which works for handles opened with and without FILE_FLAG_OVERLAPPED.
    // Each read waits for its completion, the asynchronous read of the base class runs it on the thread pool.
    sealed unsafe class Win32ReadStream(SafeFileHandle handle, long offset) : Stream
    {
        const int ERROR_HANDLE_EOF = 38;
        const int ERROR_BROKEN_PIPE = 109;
        const int ERROR_IO_PENDING = 997;

        readonly ManualResetEvent completed = new ManualResetEvent(false);
        long position = offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || buffer.Length - offset < count) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return 0;

            // The low bit of the event handle keeps the completion away from a completion port the handle is bound to.
            // The runtime owns that port and must not see an OVERLAPPED it did not allocate.
            var overlapped = new NativeOverlapped
            {
                OffsetLow = unchecked((int)position),
                OffsetHigh = (int)(position >> 32),
                EventHandle = (IntPtr)(completed.SafeWaitHandle.DangerousGetHandle().ToInt64() | 1),
            };

            int read;
            fixed (byte* p = &buffer[offset])
            {
                if (!ReadFile(handle, p, count, out read, &overlapped))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ERROR_IO_PENDING)
                    {
                        error = GetOverlappedResult(handle, &overlapped, out read, wait: true) ? 0 : Marshal.GetLastWin32Error();
                    }

                    if (error == ERROR_HANDLE_EOF || error == ERROR_BROKEN_PIPE)
                    {
                        return 0;
                    }
                    if (error != 0)
                    {
                        throw new IOException($"ReadFile failed with error {error}.", unchecked((int)0x80070000 | error));
                    }
                }
            }
            GC.KeepAlive(completed);

            position += read;
            return read;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ReadFile(SafeFileHandle handle, byte* buffer, int numberOfBytesToRead, out int numberOfBytesRead, NativeOverlapped* overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetOverlappedResult(SafeFileHandle handle, NativeOverlapped* overlapped, out int numberOfBytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
    }
}
#endif
