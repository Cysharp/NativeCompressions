using Microsoft.Win32.SafeHandles;
using NativeCompressions.LZ4.Raw;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Channels;
using static NativeCompressions.LZ4.Raw.NativeMethods;

namespace NativeCompressions.LZ4;

public static partial class LZ4
{
    const int AllowParallelCompressThreshold = 1024 * 1024; // 1MB

    static readonly StreamPipeReaderOptions LeaveOpenPipeReaderOptions = new StreamPipeReaderOptions(leaveOpen: true);

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

    // allow multithread(known size, random-access): ReadOnlyMemory, ReadOnlySequence, SafeFileHandle
    // single thread only(unknown size can't determine block-size): Stream, PipeReader

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

        if (maxDegreeOfParallelism == 1 || source.Length < AllowParallelCompressThreshold)
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
                var count = Math.Min(source.Length, actualChunkSize); // compress per chunk-size
                var buffer = destination.GetSpan(encoder.GetMaxCompressedLength(count, includingHeader: true, includingFooter: false));

                var written = encoder.Compress(source.Span.Slice(0, count), buffer);
                destination.Advance(written);

                await destination.FlushAsync(cancellationToken);
                source = source.Slice(count);
            }

            // `AutoFlush = true` so no need to care about encoder's internal buffer
            var lastBuffer = destination.GetSpan(encoder.GetActualFrameFooterLength());
            var lastWritten = encoder.Close(lastBuffer);
            destination.Advance(lastWritten);
            await destination.FlushAsync(cancellationToken);
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
            int totalBlocks = (source.Length + actualChunkSize - 1) / actualChunkSize;
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
                        var offset = unchecked(id * actualChunkSize);
                        if (offset < 0) break; // overflow

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

            var outputConsumer = RunOutputConsumerAsync(destination, dictionary, options, outputChannel, channelToken);

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

    public static async ValueTask CompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, LZ4FrameOptions? frameOptions = null, LZ4CompressionDictionary? dictionary = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        var options = frameOptions ?? LZ4FrameOptions.Default;

        // not auto-flush
        options = options with
        {
            FrameInfo = options.FrameInfo with
            {
                ContentSize = (ulong)source.Length,
                DictionaryID = dictionary?.DictionaryId ?? 0
            }
        };

        if (maxDegreeOfParallelism == 1 || source.Length < AllowParallelCompressThreshold)
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

            foreach (var sequenceBuffer in source)
            {
                var src = sequenceBuffer;
                while (!src.IsEmpty)
                {
                    var count = Math.Min(src.Length, actualChunkSize); // compress per chunk-size
                    var buffer = destination.GetSpan(encoder.GetMaxCompressedLength(count, includingHeader: true, includingFooter: false));

                    var written = encoder.Compress(src.Span.Slice(0, count), buffer);
                    if (written > 0) // flush PipeWriter when LZ4 buffer flushed
                    {
                        destination.Advance(written);
                        await destination.FlushAsync(cancellationToken);
                    }
                    src = src.Slice(count);
                }
            }

            // for auto-buffer:false, get GetMaxCompressedLength
            var lastBuffer = destination.GetSpan(encoder.GetMaxCompressedLength(0));
            var lastWritten = encoder.Close(lastBuffer);
            destination.Advance(lastWritten);
            await destination.FlushAsync(cancellationToken);
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
            var totalBlocks = (int)((source.Length + actualChunkSize - 1) / actualChunkSize);
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

                        var src = source.Slice(offset, Math.Min(remaining, actualChunkSize));
                        var bufferLength = encoder.GetMaxCompressedLength((int)src.Length, includingHeader: false, includingFooter: false);
                        var destBuffer = ArrayPool<byte>.Shared.Rent(bufferLength);

                        var written = 0;
                        var dest = destBuffer.AsSpan(0, bufferLength);
                        foreach (var item in src)
                        {
                            written += encoder.Compress(item.Span, dest);
                            dest = dest.Slice(written);
                        }
                        written += encoder.Flush(dest); // must flush after compress ReadOnlySequence chunks

                        await outputChannel.Writer.WriteAsync(new CompressionBuffer
                        {
                            CompressedBuffer = destBuffer,
                            Count = written,
                            Id = id
                        }, channelToken.Token);
                    }
                });
            }

            var outputConsumer = RunOutputConsumerAsync(destination, dictionary, options, outputChannel, channelToken);

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

    public static async ValueTask CompressAsync(SafeFileHandle source, PipeWriter destination, long offset, LZ4FrameOptions? frameOptions = null, LZ4CompressionDictionary? dictionary = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        if (source == null || source.IsInvalid || source.IsClosed)
        {
            throw new ArgumentException("Invalid file handle", nameof(source));
        }

        var options = frameOptions ?? LZ4FrameOptions.Default;
        long sourceLength = RandomAccess.GetLength(source) - offset; // we can accept `long` length(over 2GB file), don't cast to int.

        options = options with
        {
            AutoFlush = true,
            FrameInfo = options.FrameInfo with
            {
                ContentSize = (ulong)sourceLength,
                DictionaryID = dictionary?.DictionaryId ?? 0
            }
        };

        if (maxDegreeOfParallelism == 1 || sourceLength < AllowParallelCompressThreshold)
        {
            // multi-block, single-thread

            // if default block size, determine block size from source length.
            if (options.FrameInfo.BlockSizeID == BlockSizeId.Default)
            {
                options = options with
                {
                    FrameInfo = options.FrameInfo with
                    {
                        BlockSizeID = DetermineBlockSize(sourceLength, isMultiThread: false)
                    }
                };
            }

            var actualChunkSize = GetMaxBlockSize(options.FrameInfo.BlockSizeID);
            using var encoder = new LZ4Encoder(options, dictionary);

            var srcBuffer = ArrayPool<byte>.Shared.Rent(actualChunkSize);

            var remaining = sourceLength - offset;
            while (remaining != 0)
            {
                var count = (int)Math.Min(remaining, actualChunkSize); // compress per chunk-size
                var read = await RandomAccess.ReadAsync(source, srcBuffer, offset + sourceLength - remaining, cancellationToken);

                var buffer = destination.GetSpan(encoder.GetMaxCompressedLength(count, includingHeader: true, includingFooter: false));
                var written = encoder.Compress(srcBuffer.AsSpan(0, read), buffer);
                destination.Advance(written);
                await destination.FlushAsync(cancellationToken);
                remaining -= read;
            }

            // `AutoFlush = true` so no need to care about encoder's internal buffer
            var lastBuffer = destination.GetSpan(encoder.GetActualFrameFooterLength());
            var lastWritten = encoder.Close(lastBuffer);
            destination.Advance(lastWritten);
            await destination.FlushAsync(cancellationToken);
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
                        ? DetermineBlockSize(sourceLength, isMultiThread: true)
                        : options.FrameInfo.BlockSizeID,
                    BlockMode = BlockMode.BlockIndependent // for parallel
                }
            };

            var actualChunkSize = GetMaxBlockSize(options.FrameInfo.BlockSizeID);

            var threadCount = maxDegreeOfParallelism ?? Environment.ProcessorCount;
            // modify thread count for avoid too many buffer.
            var totalBlocks = (int)((sourceLength + actualChunkSize - 1) / actualChunkSize);
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
                var initialOffset = offset;
                outputProducers[i] = Task.Run(async () =>
                {
                    using var encoder = new LZ4Encoder(options, dictionary) { IsWriteHeader = false };

                    var srcBuffer = ArrayPool<byte>.Shared.Rent(actualChunkSize); // src buffer rent once
                    try
                    {
                        while (true)
                        {
                            var id = Interlocked.Increment(ref bufferId);
                            var offset = initialOffset + id * (long)actualChunkSize; // long for over 2GB file

                            var remaining = sourceLength - offset;
                            if (remaining <= 0)
                            {
                                break;
                            }

                            var read = await RandomAccess.ReadAsync(source, srcBuffer.AsMemory(0, (int)Math.Min(remaining, actualChunkSize)), offset, channelToken.Token);
                            var src = srcBuffer.AsSpan(0, read);
                            var bufferLength = encoder.GetMaxCompressedLength((int)src.Length, includingHeader: false, includingFooter: false);
                            var dest = ArrayPool<byte>.Shared.Rent(bufferLength);

                            var written = encoder.Compress(src, dest); // autoFlush

                            await outputChannel.Writer.WriteAsync(new CompressionBuffer
                            {
                                CompressedBuffer = dest,
                                Count = written,
                                Id = id
                            }, channelToken.Token);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(srcBuffer, clearArray: false);
                    }
                });
            }

            var outputConsumer = RunOutputConsumerAsync(destination, dictionary, options, outputChannel, channelToken);

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

    public static async ValueTask CompressAsync(Stream source, PipeWriter destination, LZ4FrameOptions? frameOptions = null, LZ4CompressionDictionary? dictionary = null, CancellationToken cancellationToken = default)
    {
        // change to fast-path but concurrency is always one.

        if (source is MemoryStream ms && ms.TryGetBuffer(out var buffer))
        {
            await CompressAsync((ReadOnlyMemory<byte>)buffer, destination, frameOptions, dictionary, maxDegreeOfParallelism: 1, cancellationToken);
            return;
        }

        if (source is FileStream fs && fs.CanSeek)
        {
            await CompressAsync(fs.SafeFileHandle, destination, fs.Position, frameOptions, dictionary, maxDegreeOfParallelism: 1, cancellationToken);
            return;
        }

        var pipeReader = PipeReader.Create(source, LeaveOpenPipeReaderOptions);
        await CompressAsync(pipeReader, destination, frameOptions, dictionary, cancellationToken);
        await pipeReader.CompleteAsync();
    }

    public static async ValueTask CompressAsync(PipeReader source, PipeWriter destination, LZ4FrameOptions? frameOptions = null, LZ4CompressionDictionary? dictionary = null, CancellationToken cancellationToken = default)
    {
        var options = frameOptions ?? LZ4FrameOptions.Default;

        // not auto-flush
        options = options with
        {
            FrameInfo = options.FrameInfo with
            {
                DictionaryID = dictionary?.DictionaryId ?? 0
            }
        };

        // multi-block, single-thread
        var actualChunkSize = GetMaxBlockSize(options.FrameInfo.BlockSizeID);
        using var encoder = new LZ4Encoder(options, dictionary);

        ReadResult result = default;
        while (!result.IsCompleted)
        {
            result = await source.ReadAsync(cancellationToken);
            if (result.IsCanceled) throw new OperationCanceledException();

            foreach (var sequenceBuffer in result.Buffer)
            {
                var src = sequenceBuffer;
                while (!src.IsEmpty)
                {
                    var count = Math.Min(src.Length, actualChunkSize); // compress per chunk-size
                    var buffer = destination.GetSpan(encoder.GetMaxCompressedLength(count, includingHeader: true, includingFooter: false));

                    var written = encoder.Compress(src.Span.Slice(0, count), buffer);
                    if (written > 0) // flush PipeWriter when LZ4 buffer flushed
                    {
                        destination.Advance(written);
                        await destination.FlushAsync(cancellationToken);
                    }
                    src = src.Slice(count);
                }
            }
            source.AdvanceTo(result.Buffer.End);
        }

        // for auto-buffer:false, get GetMaxCompressedLength
        var lastBuffer = destination.GetSpan(encoder.GetMaxCompressedLength(0));
        var lastWritten = encoder.Close(lastBuffer);
        destination.Advance(lastWritten);
        await destination.FlushAsync(cancellationToken);
    }

    static Task RunOutputConsumerAsync(PipeWriter destination, LZ4CompressionDictionary? dictionary, LZ4FrameOptions options, Channel<CompressionBuffer> outputChannel, CancellationTokenSource channelToken)
    {
        // common operation to write compressed buffer to destination
        return Task.Run(async () =>
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

                            await destination.WriteAsync(source.CompressedBuffer.AsMemory(0, source.Count), channelToken.Token); // write directly(don't use GetSpan/Advance API)
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
        });
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
                < 16 * 1024 * 1024 => BlockSizeId.Max1MB, // < 16MB
                _ => BlockSizeId.Max4MB
            };
        }
        else
        {
            return sourceLength switch
            {
                < 1 * 1024 * 1024 => BlockSizeId.Max64KB,     // < 1MB
                < 10 * 1024 * 1024 => BlockSizeId.Max256KB,   // < 10MB
                < 100 * 1024 * 1024 => BlockSizeId.Max1MB,    // < 100MB
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
