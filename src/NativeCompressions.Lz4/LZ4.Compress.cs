using Microsoft.Win32.SafeHandles;
using NativeCompressions.LZ4.Raw;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using static NativeCompressions.LZ4.Raw.NativeMethods;

namespace NativeCompressions.LZ4;

public static partial class LZ4
{
    public static byte[] Compress(ReadOnlySpan<byte> source) => Compress(source, LZ4FrameOptions.Default, null);

    public static unsafe byte[] Compress(ReadOnlySpan<byte> source, in LZ4FrameOptions frameOptions, LZ4CompressionDictionary? dictionary = null)
    {
        var newOptions = (dictionary == null)
            ? frameOptions.WithContentSize(source.Length)
            : frameOptions.WithContentSizeAndDictionaryId(source.Length, dictionary.DictionaryId);

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

    public static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination, in LZ4FrameOptions frameOptions, LZ4CompressionDictionary? dictionary = null)
    {
        var newOptions = (dictionary == null)
            ? frameOptions.WithContentSize(source.Length)
            : frameOptions.WithContentSizeAndDictionaryId(source.Length, dictionary.DictionaryId);
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

    // TODO: variation: ReadOnlyMemory<byte>, SafeFileHandle, Stream, ReadOnlySequence<byte>, PipeReader

    public static async ValueTask CompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, LZ4FrameOptions? frameOptions = null, LZ4CompressionDictionary? dictionary = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        var options = frameOptions ?? LZ4FrameOptions.Default;

        options = options with
        {
            AutoFlush = true, // set auto-flush
            FrameInfo = options.FrameInfo with
            {
                ContentSize = (ulong)source.Length,
                DictionaryID = dictionary?.DictionaryId ?? 0
            }
        };

        if (maxDegreeOfParallelism == 1)
        {
            // multi-block, single-thread

            // if default block size, determine block size from source length.
            if (options.FrameInfo.BlockSizeID == BlockSizeId.Default)
            {
                options = options with
                {
                    FrameInfo = options.FrameInfo with
                    {
                        BlockSizeID = DetermineBlockSize(source.Length, isMultiThread: false)
                    }
                };
            }

            var actualChunkSize = GetMaxBlockSize(options.FrameInfo.BlockSizeID);
            using var encoder = new LZ4Encoder(options, dictionary);

            while (!source.IsEmpty)
            {
                var count = Math.Min(source.Length, actualChunkSize);
                var buffer = destination.GetSpan(encoder.GetMaxCompressedLength(count, includingHeader: true, includingFooter: false));

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
        }
        else
        {
            // multi-block, multi-thread

            if (options.FrameInfo.ContentChecksumFlag == ContentChecksum.ContentChecksumEnabled)
            {
                throw new NotSupportedException("Content checksum is not supported in async compress.");
            }

            options = options with
            {
                FrameInfo = options.FrameInfo with
                {
                    BlockSizeID = (options.FrameInfo.BlockSizeID == BlockSizeId.Default)
                        ? DetermineBlockSize(source.Length, isMultiThread: true)
                        : options.FrameInfo.BlockSizeID,
                    BlockMode = BlockMode.BlockIndependent // for parallel
                }
            };

            var actualChunkSize = GetMaxBlockSize(options.FrameInfo.BlockSizeID);

            var threadCount = maxDegreeOfParallelism ?? Environment.ProcessorCount;
            // modify thread count for avoid too many buffer.
            var totalBlocks = (source.Length + actualChunkSize - 1) / actualChunkSize;
            threadCount = Math.Min(threadCount, totalBlocks);

            var capacity = threadCount * 2;

            var outputChannel = Channel.CreateBounded<CompressionBuffer>(new BoundedChannelOptions(capacity)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            using var channelToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // write header at first.
            using (var headerEncoder = new LZ4Encoder(options, dictionary) { IsWriteHeader = true })
            {
                var dest = destination.GetSpan(headerEncoder.GetActualFrameHeaderLength());
                var written = headerEncoder.Compress([], dest);
                destination.Advance(written);
                await destination.FlushAsync(cancellationToken);
            }

            // producer: slice buffer and compress, send compressed buffer.
            var bufferId = -1;
            var outputProducers = new Task[threadCount];
            for (int i = 0; i < outputProducers.Length; i++)
            {
                outputProducers[i] = Task.Run(async () =>
                {
                    using var encoder = new LZ4Encoder(options, dictionary) { IsWriteHeader = false };

                    while (true)
                    {
                        var id = Interlocked.Increment(ref bufferId);
                        var offset = id * actualChunkSize;

                        var remaining = source.Length - offset;
                        if (remaining <= 0)
                        {
                            break;
                        }

                        var src = source.Span.Slice(offset, Math.Min(remaining, actualChunkSize));
                        var bufferLength = encoder.GetMaxCompressedLength(src.Length, includingHeader: false, includingFooter: false);
                        var dest = ArrayPool<byte>.Shared.Rent(bufferLength);

                        var written = encoder.Compress(src, dest);

                        await outputChannel.Writer.WriteAsync(new CompressionBuffer
                        {
                            CompressedBuffer = dest,
                            Count = written,
                            Id = id
                        }, channelToken.Token);
                    }
                });
            }

            var outputConsumer = Task.Run(async () =>
            {
                var nextId = 0; // id for write
                var reader = outputChannel.Reader;
                var buffers = new MiniPriorityQueue<CompressionBuffer>();
                try
                {
                    while (await reader.WaitToReadAsync(channelToken.Token))
                    {
                        while (reader.TryRead(out var compressedBuffer))
                        {
                            buffers.Enqueue(compressedBuffer);

                            while (buffers.Count > 0 && buffers.Peek().Id == nextId)
                            {
                                var source = buffers.Dequeue();
                                nextId++;

                                await destination.WriteAsync(source.CompressedBuffer.AsMemory(0, source.Count), channelToken.Token);
                                ArrayPool<byte>.Shared.Return(source.CompressedBuffer, clearArray: false);
                            }
                        }
                    }
                }
                finally
                {
                    // if buffer is remained, return to pool.
                    foreach (var item in buffers.Values)
                    {
                        ArrayPool<byte>.Shared.Return(item.CompressedBuffer, clearArray: false);
                    }
                }

                // channel complete, flush(ConentSize = 0 to ignore verify size)
                using var encoder = new LZ4Encoder(options with { FrameInfo = options.FrameInfo with { ContentSize = 0 } }, dictionary) { IsWriteHeader = false };
                var lastBuffer = destination.GetSpan(encoder.GetActualFrameFooterLength());
                var lastWritten = encoder.Close(lastBuffer);
                destination.Advance(lastWritten);

                await destination.FlushAsync(channelToken.Token);
                await destination.CompleteAsync();
            });

            try
            {
                await Task.WhenAll(outputProducers);
                outputChannel.Writer.Complete(); // all reader complete, input is finished.
                await outputConsumer; // wait for complete flush compressed data
            }
            catch
            {
                channelToken.Cancel(); // when any exception, cancel all tasks.
                throw;
            }
        }
    }

    static int GetMaxBlockSize(BlockSizeId id)
    {
        switch (id)
        {
            case BlockSizeId.Default:
            case BlockSizeId.Max64KB:
                return 64 * 1024;
            case BlockSizeId.Max256KB:
                return 256 * 1024;
            case BlockSizeId.Max1MB:
                return 1024 * 1024;
            case BlockSizeId.Max4MB:
                return 4 * 1024 * 1024;
            default:
                throw new LZ4Exception("Invalid blockSize");
        }
    }

    static BlockSizeId DetermineBlockSize(long sourceLength, bool isMultiThread)
    {
        if (isMultiThread)
        {
            return sourceLength switch
            {
                < 16 * 1024 * 1024 => BlockSizeId.Max1MB,
                _ => BlockSizeId.Max4MB
            };
        }
        else
        {
            return sourceLength switch
            {
                < 1 * 1024 * 1024 => BlockSizeId.Max64KB,
                < 10 * 1024 * 1024 => BlockSizeId.Max256KB,
                < 100 * 1024 * 1024 => BlockSizeId.Max1MB,
                _ => BlockSizeId.Max4MB
            };
        }
    }

    struct CompressionBuffer : IComparable<CompressionBuffer>
    {
        public int Id;
        public byte[] CompressedBuffer;
        public int Count;

        public int CompareTo(CompressionBuffer other)
        {
            return Id.CompareTo(other.Id);
        }

        public override string ToString()
        {
            return Id.ToString();
        }
    }
}
