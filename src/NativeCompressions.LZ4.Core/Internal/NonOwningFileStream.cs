#if NETSTANDARD
using Microsoft.Win32.SafeHandles;

namespace NativeCompressions.Internal;

// netstandard2.1 has no RandomAccess, so a caller's SafeFileHandle is read through a FileStream.
// The stream uses the caller's handle directly: wrapping the raw handle in a new SafeFileHandle would lose
// the thread pool binding of an asynchronous handle and rebinding fails. FileStream would close the handle
// when disposed or finalized, so the returned stream must never be disposed and its finalizer is suppressed.
internal static class NonOwningFileStream
{
    public static FileStream Open(SafeFileHandle handle, long offset)
    {
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
        GC.SuppressFinalize(stream);

        // always seek, the handle's own file position is unrelated to the requested offset
        stream.Position = offset;
        return stream;
    }
}
#endif
