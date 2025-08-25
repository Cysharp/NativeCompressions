using Microsoft.Win32.SafeHandles;
using NativeCompressions.LZ4.Raw;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using static NativeCompressions.LZ4.Raw.NativeMethods;

namespace NativeCompressions.LZ4;

public static partial class LZ4
{
    const int CompressChunkSize = 4 * 1024 * 1024; // 4MB

    public static byte[] Compress(ReadOnlySpan<byte> source) => Compress(source, LZ4FrameOptions.Default, null);

    public static unsafe byte[] Compress(ReadOnlySpan<byte> source, in LZ4FrameOptions options, LZ4CompressionDictionary? dictionary)
    {
        var newOptions = (dictionary == null)
            ? options.WithContentSize(source.Length)
            : options.WithContentSizeAndDictionaryId(source.Length, dictionary.DictionaryId);

        var pref = newOptions.ToPreferences();
        var maxLength = LZ4F_compressFrameBound((uint)source.Length, pref);

        var buffer = ArrayPool<byte>.Shared.Rent((int)maxLength);
        try
        {
            fixed (byte* src = source)
            fixed (byte* dest = buffer)
            {
                if (dictionary == null)
                {
                    var bytesWrittenOrErrorCode = LZ4F_compressFrame(dest, (nuint)buffer.Length, src, (nuint)source.Length, pref);
                    HandleErrorCode(bytesWrittenOrErrorCode);
                    return buffer.AsSpan(0, (int)bytesWrittenOrErrorCode).ToArray();
                }
                else
                {
                    LZ4F_cctx_s* cctx = default;
                    var code = LZ4F_createCompressionContext(&cctx, LZ4.FrameVersion);
                    LZ4.HandleErrorCode(code);
                    try
                    {
                        var bytesWrittenOrErrorCode = LZ4F_compressFrame_usingCDict(cctx, dest, (nuint)buffer.Length, src, (nuint)source.Length, dictionary.Handle, pref);
                        HandleErrorCode(bytesWrittenOrErrorCode);
                        return buffer.AsSpan(0, (int)bytesWrittenOrErrorCode).ToArray();
                    }
                    finally
                    {
                        LZ4F_freeCompressionContext(cctx);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination) => Compress(source, destination, LZ4FrameOptions.Default, null);

    public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination, in LZ4FrameOptions options, LZ4CompressionDictionary? dictionary)
    {
        var newOptions = (dictionary == null)
            ? options.WithContentSize(source.Length)
            : options.WithContentSizeAndDictionaryId(source.Length, dictionary.DictionaryId);
        var pref = newOptions.ToPreferences();

        fixed (byte* src = source)
        fixed (byte* dest = destination)
        {
            if (dictionary == null)
            {
                var bytesWrittenOrErrorCode = LZ4F_compressFrame(dest, (nuint)destination.Length, src, (nuint)source.Length, pref);
                HandleErrorCode(bytesWrittenOrErrorCode);
                return (int)bytesWrittenOrErrorCode;
            }
            else
            {
                LZ4F_cctx_s* cctx = default;
                var code = LZ4F_createCompressionContext(&cctx, LZ4.FrameVersion);
                LZ4.HandleErrorCode(code);
                try
                {
                    var bytesWrittenOrErrorCode = LZ4F_compressFrame_usingCDict(cctx, dest, (nuint)destination.Length, src, (nuint)source.Length, dictionary.Handle, pref);
                    HandleErrorCode(bytesWrittenOrErrorCode);
                    return (int)bytesWrittenOrErrorCode;
                }
                finally
                {
                    LZ4F_freeCompressionContext(cctx);
                }
            }
        }
    }

    // TODO: SafeFileHandle
    // public static async ValueTask CompressAsync(SafeFileHandle source, PipeWriter destination, LZ4FrameOptions options, LZ4CompressionDictionary? dictionary, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default);
    // public static async ValueTask CompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, LZ4FrameOptions options, LZ4CompressionDictionary? dictionary, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default);

    public static async ValueTask CompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, LZ4FrameOptions options, LZ4CompressionDictionary? dictionary, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        options = options with
        {
            AutoFlush = true, // set auto-flush
            FrameInfo = options.FrameInfo with
            {
                ContentSize = (ulong)source.Length,
                DictionaryID = dictionary?.DictionaryId ?? 0
            }
        };

        if (source.Length < CompressChunkSize)
        {
            // compress as single-block
            try
            {
                var size = LZ4.GetMaxCompressedLength(source.Length, options);
                var dest = destination.GetSpan(size);
                var written = LZ4.Compress(source.Span, dest, options, dictionary);
                destination.Advance(written);
            }
            catch (Exception ex)
            {
                await destination.CompleteAsync(ex);
                return;
            }

            await destination.FlushAsync(cancellationToken);
            await destination.CompleteAsync();
            return;
        }

        // multi-block, single-thread
        if (maxDegreeOfParallelism == 1)
        {
            options = options with
            {
                FrameInfo = options.FrameInfo with
                {
                    BlockSizeID = BlockSizeId.Max4MB // use 4MB
                }
            };

            using var encoder = new LZ4Encoder(options, dictionary);

            while (!source.IsEmpty)
            {
                var count = Math.Min(source.Length, CompressChunkSize);
                var buffer = destination.GetSpan(count);

                var written = encoder.Compress(source.Span.Slice(0, count), buffer);
                destination.Advance(written);

                var status = await destination.FlushAsync(cancellationToken);
                if (status.IsCanceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                source = source.Slice(count);
            }

            // `AutoFlush = true` so no need to care about encoder's internal buffer
            var lastBuffer = destination.GetSpan(encoder.GetActualFrameFooterLength());
            var lastWritten = encoder.Close(lastBuffer);
            destination.Advance(lastWritten);

            await destination.FlushAsync(cancellationToken);
            await destination.CompleteAsync();
            return;
        }
        else
        {
            // multi-block, multi-thread
            options = options with
            {
                FrameInfo = options.FrameInfo with
                {
                    BlockSizeID = BlockSizeId.Max4MB, // use 4MB
                    BlockMode = BlockMode.BlockIndependent // for parallel
                }
            };

            var threadCount = maxDegreeOfParallelism ?? Environment.ProcessorCount;

            // TODO: use BoundedChannel?
            var outputChannel = Channel.CreateUnbounded<MultithreadCompressionBuffer>(new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = true,
            });

            using (var headerEncoder = new LZ4Encoder(options, dictionary) { IsWriteHeader = true })
            {
                var dest = destination.GetSpan(headerEncoder.GetActualFrameHeaderLength());
                var written = headerEncoder.Compress([], dest);
                destination.Advance(written);
                await destination.FlushAsync(cancellationToken);
            }

            var writerTask = Task.Run(async () =>
            {
                var nextId = 0; // id for write
                var reader = outputChannel.Reader;
                var buffers = new List<MultithreadCompressionBuffer>();
                while (await reader.WaitToReadAsync(cancellationToken))
                {
                    while (reader.TryRead(out var compressedBuffer))
                    {
                        buffers.Add(compressedBuffer);
                        buffers.Sort(); // reverse-order

                        while (buffers.Count > 0 && buffers[^1].Id == nextId)
                        {
                            var source = buffers[^1];
                            await destination.WriteAsync(source.CompressedBuffer.AsMemory(0, source.Count), cancellationToken);
                            buffers.RemoveAt(buffers.Count - 1); // Remove from last is performant than remove first
                            ArrayPool<byte>.Shared.Return(source.CompressedBuffer, clearArray: false);
                            nextId++;
                        }
                    }
                }

                // channel complete, flush
                var encoder = new LZ4Encoder(options, dictionary) { IsWriteHeader = false };
                var lastBuffer = destination.GetSpan(encoder.GetActualFrameFooterLength());
                var lastWritten = encoder.Close(lastBuffer);
                destination.Advance(lastWritten);

                await destination.FlushAsync(cancellationToken);
                await destination.CompleteAsync();
            });

            var bufferId = -1;
            var readerTasks = new Task[threadCount];
            for (int i = 0; i < readerTasks.Length; i++)
            {
                readerTasks[i] = Task.Run(() =>
                {
                    using var encoder = new LZ4Encoder(options, dictionary) { IsWriteHeader = false };

                    while (true)
                    {
                        var id = Interlocked.Increment(ref bufferId);
                        var offset = id * CompressChunkSize;

                        var sourceLength = source.Length - offset;
                        if (sourceLength <= 0)
                        {
                            break;
                        }

                        var src = source.Span.Slice(offset, Math.Min(source.Length - offset, CompressChunkSize));
                        var bufferLength = encoder.GetMaxCompressedLength(src.Length, includingHeaderAndFooter: false);
                        var dest = ArrayPool<byte>.Shared.Rent(bufferLength);

                        var written = encoder.Compress(src, dest);

                        outputChannel.Writer.TryWrite(new MultithreadCompressionBuffer
                        {
                            CompressedBuffer = dest,
                            Count = written,
                            Id = id
                        });
                    }
                });
            }

            await Task.WhenAll(readerTasks);

            // all reader complete, input is finished.
            outputChannel.Writer.Complete();

            // wait for complete flush compressed data
            await writerTask;
        }
    }

    // use PriorityQueue is better but to support netstandard2.1
    internal struct MultithreadCompressionBuffer : IComparable<MultithreadCompressionBuffer>
    {
        public int Id;
        public byte[] CompressedBuffer;
        public int Count;

        public int CompareTo(MultithreadCompressionBuffer other)
        {
            // reverse-order
            if (Id < other.Id) return 1;
            if (Id > other.Id) return -1;
            return 0;
        }
    }
}
