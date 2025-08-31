using Microsoft.Win32.SafeHandles;
using NativeCompressions.LZ4.Internal;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;

namespace NativeCompressions.LZ4;

public static partial class LZ4
{
    public static unsafe byte[] Decompress(ReadOnlySpan<byte> source, LZ4CompressionDictionary? dictionary = null, bool trustedData = false)
    {
        using var decoder = new LZ4Decoder(dictionary);

        if (trustedData)
        {
            var frameInfo = decoder.GetFrameInfo(source, out var consumed);
            source = source.Slice(consumed);

            if (frameInfo.ContentSize != 0) // 0 means unknown
            {
                var destination = new byte[frameInfo.ContentSize]; // trusted ContentSize, decode one-shot.
                var dest = destination.AsSpan();
                var status = OperationStatus.DestinationTooSmall;
                while (status == OperationStatus.DestinationTooSmall && source.Length > 0)
                {
                    status = decoder.Decompress(source, dest, out var bytesConsumed, out var bytesWritten);
                    source = source.Slice(bytesConsumed);
                    dest = dest.Slice(bytesWritten);
                }

                if (status == OperationStatus.NeedMoreData)
                {
                    throw new InvalidOperationException("Invalid LZ4 frame.");
                }

                return destination;
            }
        }

        {
            Span<byte> scratch = stackalloc byte[256];
            var arrayProvider = new SegmentedArrayProvider<byte>(scratch);

            var dest = arrayProvider.GetSpan();
            var status = OperationStatus.DestinationTooSmall;
            while (status == OperationStatus.DestinationTooSmall && source.Length > 0)
            {
                status = decoder.Decompress(source, dest, out var bytesConsumed, out var bytesWritten);
                if (bytesWritten == 0 && bytesConsumed == 0 && status == OperationStatus.DestinationTooSmall)
                {
                    throw new InvalidOperationException("Decoder stuck");
                }

                source = source.Slice(bytesConsumed);
                dest = dest.Slice(bytesWritten);
                arrayProvider.Advance(bytesWritten);

                if (dest.Length == 0)
                {
                    dest = arrayProvider.GetSpan();
                }
            }

            if (status == OperationStatus.NeedMoreData)
            {
                throw new InvalidOperationException("Invalid LZ4 frame.");
            }

            var result = GC.AllocateUninitializedArray<byte>(arrayProvider.Count);
            arrayProvider.CopyToAndClear(result);
            return result;
        }
    }

    public static unsafe int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, LZ4CompressionDictionary? dictionary = null)
    {
        using var decoder = new LZ4Decoder(dictionary);

        var totalWritten = 0;
        var status = OperationStatus.DestinationTooSmall;
        while (status == OperationStatus.DestinationTooSmall && source.Length > 0)
        {
            status = decoder.Decompress(source, destination, out var bytesConsumed, out var bytesWritten);
            if (bytesWritten == 0 && bytesConsumed == 0 && status == OperationStatus.DestinationTooSmall)
            {
                throw new InvalidOperationException("Decoder stuck");
            }

            source = source.Slice(bytesConsumed);
            destination = destination.Slice(bytesWritten);
            totalWritten += bytesWritten;
        }

        if (status == OperationStatus.NeedMoreData)
        {
            throw new InvalidOperationException("Invalid LZ4 frame.");
        }

        return totalWritten;
    }

    public static async ValueTask DecompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, LZ4CompressionDictionary? dictionary = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new LZ4Decoder(dictionary);

        var frameInfo = decoder.GetFrameInfo(source.Span, out var bytesConsumed);
        source = source.Slice(bytesConsumed);

        var maxBlockSize = GetMaxBlockSize(frameInfo.BlockSizeID);

        var checksumSize = (frameInfo.BlockChecksumFlag == BlockChecksum.BlockChecksumEnabled)
            ? 4
            : 0;

        var supportMultithreadDecode = frameInfo.BlockMode == BlockMode.BlockIndependent && (maxDegreeOfParallelism == null || maxDegreeOfParallelism > 1);

        if (!supportMultithreadDecode)
        {
            var status = OperationStatus.DestinationTooSmall;
            while (status == OperationStatus.DestinationTooSmall && source.Length > 0)
            {
                var dest = destination.GetSpan(maxBlockSize);

                status = decoder.Decompress(source.Span, dest, out bytesConsumed, out var bytesWritten);
                if (bytesWritten == 0 && bytesConsumed == 0 && status == OperationStatus.DestinationTooSmall)
                {
                    throw new InvalidOperationException("Decoder stuck");
                }
                source = source.Slice(bytesConsumed);
                destination.Advance(bytesWritten);
                await destination.FlushAsync(cancellationToken);
            }

            if (status == OperationStatus.NeedMoreData)
            {
                throw new InvalidOperationException("Invalid LZ4 frame.");
            }
        }
        else
        {
            var threadCount = maxDegreeOfParallelism ?? Environment.ProcessorCount;
            var capacity = threadCount * 2;

            var inputChannel = Channel.CreateBounded<DecompressionInputBuffer>(new BoundedChannelOptions(capacity)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            var outputChannel = Channel.CreateBounded<DecompressionOutputBuffer>(new BoundedChannelOptions(capacity)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            using var channelToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var inputProducer = Task.Run(async () =>
            {
                var id = 0;
                while (source.Length != 0)
                {
                    var blockHeader = ReadBlockHeader(source.Span);
                    if (blockHeader.IsEndMark)
                    {
                        break;
                    }

                    var nextBlockOffset = blockHeader.CompressedSize + checksumSize;
                    var item = new DecompressionInputBuffer()
                    {
                        Id = id,
                        IsUncompressed = blockHeader.IsUncompressed,
                        CompressedBuffer = source.Slice(4, nextBlockOffset - checksumSize), // skip block header, checksum
                        IsBufferRentFromPool = false // slice from original
                    };

                    await inputChannel.Writer.WriteAsync(item, channelToken.Token);

                    source = source.Slice(4 + nextBlockOffset);
                    id++;
                }
                inputChannel.Writer.Complete();
            });

            Task[] inputConsumerOutputProducers = StartDecompressBlock(dictionary, maxBlockSize, threadCount, inputChannel, outputChannel, channelToken);
            Task outputConsumer = StartWriteDecompressedBuffer(destination, outputChannel, channelToken);

            try
            {
                await inputProducer;
                await Task.WhenAll(inputConsumerOutputProducers);
                outputChannel.Writer.Complete();
                await outputConsumer;
            }
            catch
            {
                channelToken.Cancel(); // when any exception, cancel all tasks.
                throw;
            }
        }
    }

    public static async ValueTask DecompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, LZ4CompressionDictionary? dictionary = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new LZ4Decoder(dictionary);

        Span<byte> temp = stackalloc byte[GetMaxFrameHeaderLength()];
        source.Slice(0, Math.Min(source.Length, temp.Length)).CopyTo(temp);

        var frameInfo = decoder.GetFrameInfo(temp, out var bytesConsumed); // if temp is too small, LZ4F_getFrameInfo returns error.
        source = source.Slice(bytesConsumed);

        var maxBlockSize = GetMaxBlockSize(frameInfo.BlockSizeID);

        var checksumSize = (frameInfo.BlockChecksumFlag == BlockChecksum.BlockChecksumEnabled)
            ? 4
            : 0;

        var supportMultithreadDecode = frameInfo.BlockMode == BlockMode.BlockIndependent && (maxDegreeOfParallelism == null || maxDegreeOfParallelism > 1);

        if (!supportMultithreadDecode)
        {
            var status = OperationStatus.DestinationTooSmall;
            var destWritten = 0;
            var dest = destination.GetSpan(maxBlockSize); // decompress per block
            foreach (var srcBuffer in source)
            {
                var src = srcBuffer;
                while (src.Length > 0)
                {
                    status = decoder.Decompress(src.Span, dest, out bytesConsumed, out var bytesWritten);
                    if (bytesWritten == 0 && bytesConsumed == 0 && status == OperationStatus.DestinationTooSmall)
                    {
                        throw new InvalidOperationException("Decoder stuck");
                    }
                    src = src.Slice(bytesConsumed);
                    dest = dest.Slice(bytesWritten);
                    destWritten += bytesWritten;

                    if (dest.Length == 0)
                    {
                        destination.Advance(destWritten);
                        await destination.FlushAsync(cancellationToken);
                        dest = destination.GetSpan(maxBlockSize);
                        destWritten = 0;
                    }
                    if (status == OperationStatus.Done)
                    {
                        goto END;
                    }
                }
            }

        END:
            if (destWritten > 0)
            {
                destination.Advance(destWritten);
                await destination.FlushAsync(cancellationToken);
            }

            if (status == OperationStatus.NeedMoreData)
            {
                throw new InvalidOperationException("Invalid LZ4 frame.");
            }
        }
        else
        {
            var threadCount = maxDegreeOfParallelism ?? Environment.ProcessorCount;
            var capacity = threadCount * 2;

            var inputChannel = Channel.CreateBounded<DecompressionInputBuffer>(new BoundedChannelOptions(capacity)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            var outputChannel = Channel.CreateBounded<DecompressionOutputBuffer>(new BoundedChannelOptions(capacity)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            using var channelToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var inputProducer = Task.Run(async () =>
            {
                var id = 0;
                while (source.Length != 0)
                {
                    var blockHeader = ReadBlockHeader(source);
                    if (blockHeader.IsEndMark)
                    {
                        break;
                    }

                    var compressedBuffer = ArrayPool<byte>.Shared.Rent(blockHeader.CompressedSize);
                    source.Slice(4, blockHeader.CompressedSize).CopyTo(compressedBuffer); // skip header

                    var item = new DecompressionInputBuffer()
                    {
                        Id = id,
                        IsUncompressed = blockHeader.IsUncompressed,
                        CompressedBuffer = compressedBuffer.AsMemory(0, blockHeader.CompressedSize),
                        IsBufferRentFromPool = true
                    };

                    await inputChannel.Writer.WriteAsync(item, channelToken.Token);

                    source = source.Slice(4 + blockHeader.CompressedSize + checksumSize); // header + data + footer
                    id++;
                }
                inputChannel.Writer.Complete();
            });

            Task[] inputConsumerOutputProducers = StartDecompressBlock(dictionary, maxBlockSize, threadCount, inputChannel, outputChannel, channelToken);
            Task outputConsumer = StartWriteDecompressedBuffer(destination, outputChannel, channelToken);

            try
            {
                await inputProducer;
                await Task.WhenAll(inputConsumerOutputProducers);
                outputChannel.Writer.Complete();
                await outputConsumer;
            }
            catch
            {
                channelToken.Cancel(); // when any exception, cancel all tasks.
                throw;
            }
        }
    }

    public static ValueTask DecompressAsync(SafeFileHandle source, PipeWriter destination, LZ4CompressionDictionary? dictionary = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        return DecompressAsync(source, 0, destination, dictionary, maxDegreeOfParallelism, cancellationToken);
    }

    public static async ValueTask DecompressAsync(SafeFileHandle source, long offset, PipeWriter destination, LZ4CompressionDictionary? dictionary = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new LZ4Decoder(dictionary);

        var sourceLength = RandomAccess.GetLength(source);

        Span<byte> headerBuffer = stackalloc byte[LZ4.GetMaxFrameHeaderLength()];
        var headerRead = RandomAccess.Read(source, headerBuffer.Slice(0, LZ4.GetMaxFrameHeaderLength()), offset); // read(oversize)

        var frameInfo = decoder.GetFrameInfo(headerBuffer.Slice(0, headerRead), out var bytesConsumed); // if short size, LZ4F_getFrameInfo returns error
        offset += bytesConsumed;

        var maxBlockSize = GetMaxBlockSize(frameInfo.BlockSizeID);

        var checksumSize = (frameInfo.BlockChecksumFlag == BlockChecksum.BlockChecksumEnabled)
            ? 4
            : 0;

        var supportMultithreadDecode = frameInfo.BlockMode == BlockMode.BlockIndependent && (maxDegreeOfParallelism == null || maxDegreeOfParallelism > 1);

        if (!supportMultithreadDecode)
        {
            var remains = sourceLength - offset;

            var sourceBuffer = ArrayPool<byte>.Shared.Rent(4 + maxBlockSize + checksumSize);
            try
            {
                var dest = destination.GetMemory(maxBlockSize);
                var destWritten = 0;

                var status = OperationStatus.DestinationTooSmall;
                while (remains > 0)
                {
                    var read = await RandomAccess.ReadAsync(source, sourceBuffer, offset, cancellationToken);
                    if (read == 0) break;

                    offset += read;
                    var src = sourceBuffer.AsMemory(0, read);

                    while (src.Length > 0 && remains > 0)
                    {
                        status = decoder.Decompress(src.Span, dest.Span, out bytesConsumed, out var bytesWritten, out var sizeHint);
                        if (bytesWritten == 0 && bytesConsumed == 0 && status == OperationStatus.DestinationTooSmall)
                        {
                            throw new InvalidOperationException("Decoder stuck");
                        }

                        src = src.Slice(bytesConsumed);
                        dest = dest.Slice(bytesWritten);
                        remains -= bytesConsumed;
                        destWritten += bytesWritten;

                        if (dest.Length == 0)
                        {
                            destination.Advance(destWritten);
                            await destination.FlushAsync(cancellationToken);
                            dest = destination.GetMemory(maxBlockSize);
                            destWritten = 0;
                        }
                    }
                }

                if (destWritten > 0)
                {
                    destination.Advance(destWritten);
                    await destination.FlushAsync(cancellationToken);
                    dest = destination.GetMemory(maxBlockSize);
                }

                if (status == OperationStatus.NeedMoreData)
                {
                    throw new InvalidOperationException("Invalid LZ4 frame.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sourceBuffer, clearArray: false);
            }
        }
        else
        {
            var threadCount = maxDegreeOfParallelism ?? Environment.ProcessorCount;
            var capacity = threadCount * 2;

            var inputChannel = Channel.CreateBounded<DecompressionInputBuffer>(new BoundedChannelOptions(capacity)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            var outputChannel = Channel.CreateBounded<DecompressionOutputBuffer>(new BoundedChannelOptions(capacity)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            using var channelToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var remains = sourceLength - offset;

            var inputProducer = Task.Run(async () =>
            {
                var id = 0;
                var sourceBuffer = ArrayPool<byte>.Shared.Rent(4 + maxBlockSize + checksumSize);
                try
                {
                    var src = Memory<byte>.Empty;
                    while (remains != 0)
                    {
                        if (src.Length <= 3) // need to read header
                        {
                            if (src.Length == 0)
                            {
                                var read = await RandomAccess.ReadAsync(source, sourceBuffer, offset, cancellationToken);
                                offset += read;
                                src = sourceBuffer.AsMemory(0, read);
                            }
                            else
                            {
                                // copy-to-head
                                src.Span.CopyTo(sourceBuffer);
                                var read = await RandomAccess.ReadAsync(source, sourceBuffer.AsMemory(src.Length), offset, cancellationToken);
                                offset += read;
                                src = sourceBuffer.AsMemory(0, src.Length + read);
                            }
                        }

                        var blockHeader = ReadBlockHeader(src.Span);
                        if (blockHeader.IsEndMark)
                        {
                            break;
                        }

                        var compressedBuffer = ArrayPool<byte>.Shared.Rent(blockHeader.CompressedSize);

                        if (blockHeader.CompressedSize + checksumSize > src.Length - 4)
                        {
                            // need to read more
                            src.Span.Slice(4).CopyTo(compressedBuffer); // copy existing
                            var copiedBytes = src.Length - 4;
                            var read = await RandomAccess.ReadAsync(source, compressedBuffer.AsMemory(copiedBytes, blockHeader.CompressedSize - copiedBytes), offset, cancellationToken);
                            offset += (read + checksumSize); // skip checksum
                            src = default;
                        }
                        else
                        {
                            // enough data in src
                            src.Slice(4, blockHeader.CompressedSize).CopyTo(compressedBuffer);
                            src = src.Slice(4 + blockHeader.CompressedSize + checksumSize);
                        }

                        var item = new DecompressionInputBuffer()
                        {
                            Id = id,
                            IsUncompressed = blockHeader.IsUncompressed,
                            CompressedBuffer = compressedBuffer.AsMemory(0, blockHeader.CompressedSize),
                            IsBufferRentFromPool = true
                        };

                        await inputChannel.Writer.WriteAsync(item, channelToken.Token);
                        remains -= (4 + blockHeader.CompressedSize + checksumSize);
                        id++;
                    }

                    inputChannel.Writer.Complete();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(sourceBuffer, clearArray: false);
                }
            });

            Task[] inputConsumerOutputProducers = StartDecompressBlock(dictionary, maxBlockSize, threadCount, inputChannel, outputChannel, channelToken);
            Task outputConsumer = StartWriteDecompressedBuffer(destination, outputChannel, channelToken);

            try
            {
                await inputProducer;
                await Task.WhenAll(inputConsumerOutputProducers);
                outputChannel.Writer.Complete();
                await outputConsumer;
            }
            catch
            {
                channelToken.Cancel(); // when any exception, cancel all tasks.
                throw;
            }
        }
    }

    public static async ValueTask DecompressAsync(Stream source, PipeWriter destination, LZ4CompressionDictionary? dictionary = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        // change to fast-path

        if (source is MemoryStream ms && ms.TryGetBuffer(out var buffer))
        {
            await DecompressAsync((ReadOnlyMemory<byte>)buffer, destination, dictionary, maxDegreeOfParallelism, cancellationToken);
            return;
        }

        if (source is FileStream fs && fs.CanSeek)
        {
            await DecompressAsync(fs.SafeFileHandle, fs.Position, destination, dictionary, maxDegreeOfParallelism, cancellationToken);
            return;
        }

        var pipeReader = PipeReader.Create(source, LeaveOpenPipeReaderOptions);
        await DecompressAsync(pipeReader, destination, dictionary, maxDegreeOfParallelism, cancellationToken);
        await pipeReader.CompleteAsync();
    }

    public static async ValueTask DecompressAsync(PipeReader source, PipeWriter destination, LZ4CompressionDictionary? dictionary = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new LZ4Decoder(dictionary);

        LZ4FrameInfo frameInfo;

        // get header.
        {
            // pick min frame-header
            var result = await source.ReadAtLeastAsync(LZ4.GetMinSizeToKnowFrameHeaderLength(), cancellationToken);

            // to simplify implementation, always copy to working buffer from ReadOnlySpan<byte>
            Span<byte> headerBuffer = stackalloc byte[LZ4.GetMaxFrameHeaderLength()];
            result.Buffer.Slice(0, LZ4.GetMinSizeToKnowFrameHeaderLength()).CopyTo(headerBuffer);
            source.AdvanceTo(result.Buffer.Start); // no-advance

            var headerSize = decoder.GetHeaderSize(headerBuffer);

            // pick actual frame-header
            result = await source.ReadAtLeastAsync(headerSize, cancellationToken);

            Span<byte> headerBuffer2 = stackalloc byte[LZ4.GetMaxFrameHeaderLength()]; // don't trust headerSize for untrusted data
            result.Buffer.Slice(0, headerSize).CopyTo(headerBuffer2);

            frameInfo = decoder.GetFrameInfo(headerBuffer2, out var consumed);
            source.AdvanceTo(result.Buffer.GetPosition(consumed));
        }

        var maxBlockSize = GetMaxBlockSize(frameInfo.BlockSizeID);

        var checksumSize = (frameInfo.BlockChecksumFlag == BlockChecksum.BlockChecksumEnabled)
            ? 4
            : 0;

        var supportMultithreadDecode = frameInfo.BlockMode == BlockMode.BlockIndependent && (maxDegreeOfParallelism == null || maxDegreeOfParallelism > 1);

        if (!supportMultithreadDecode)
        {
            // single-thread decompress
            var status = OperationStatus.DestinationTooSmall;
            var dest = destination.GetMemory(maxBlockSize);
            var writtenCount = 0; // written count in this span

            ReadResult result = default;
            while (!result.IsCompleted) // Read Loop
            {
                result = await source.ReadAsync(cancellationToken);
                if (result.IsCanceled) throw new OperationCanceledException();

                var consumedInBuffer = 0;
                foreach (var sequenceBuffer in result.Buffer) // Read(Buffer) Loop
                {
                    var src = sequenceBuffer;
                    while (src.Length > 0) // Decompress Loop
                    {
                        status = decoder.Decompress(src.Span, dest.Span, out var bytesConsumed, out var bytesWritten);
                        if (bytesWritten == 0 && bytesConsumed == 0 && status == OperationStatus.DestinationTooSmall)
                        {
                            throw new InvalidOperationException("Decoder stuck");
                        }

                        src = src.Slice(bytesConsumed);
                        dest = dest.Slice(bytesWritten);
                        consumedInBuffer += bytesConsumed;
                        writtenCount += bytesWritten;

                        if (dest.Length == 0)
                        {
                            destination.Advance(writtenCount);
                            await destination.FlushAsync(cancellationToken);
                            dest = destination.GetMemory(maxBlockSize);
                            writtenCount = 0;
                        }
                        if (status == OperationStatus.Done)
                        {
                            source.AdvanceTo(result.Buffer.GetPosition(consumedInBuffer));
                            goto END;
                        }
                    }
                }
                consumedInBuffer = 0;
                source.AdvanceTo(result.Buffer.End);
            }
        END:

            // flush final bytes
            if (writtenCount > 0)
            {
                destination.Advance(writtenCount);
                await destination.FlushAsync(cancellationToken);
            }

            if (status == OperationStatus.NeedMoreData)
            {
                throw new InvalidOperationException("Invalid LZ4 frame.");
            }

        }
        else
        {
            // multi-thread decompress
            var threadCount = maxDegreeOfParallelism ?? Environment.ProcessorCount;
            var capacity = threadCount * 2;

            var inputChannel = Channel.CreateBounded<DecompressionInputBuffer>(new BoundedChannelOptions(capacity)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            var outputChannel = Channel.CreateBounded<DecompressionOutputBuffer>(new BoundedChannelOptions(capacity)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            using var channelToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var inputProducer = Task.Run(async () =>
            {
                var id = 0;
                while (true)
                {
                    var blockHeader = await ReadBlockHeaderAsync(source, cancellationToken);
                    if (blockHeader.IsEndMark) break;

                    var nextBlockOffset = blockHeader.CompressedSize + checksumSize;

                    var readResult = await source.ReadAtLeastAsync(nextBlockOffset, channelToken.Token);

                    var compressedBuffer = ArrayPool<byte>.Shared.Rent(blockHeader.CompressedSize);
                    readResult.Buffer.Slice(0, blockHeader.CompressedSize).CopyTo(compressedBuffer);

                    var item = new DecompressionInputBuffer()
                    {
                        Id = id,
                        IsUncompressed = blockHeader.IsUncompressed,
                        CompressedBuffer = compressedBuffer.AsMemory(0, blockHeader.CompressedSize),
                        IsBufferRentFromPool = true,
                    };

                    await inputChannel.Writer.WriteAsync(item, channelToken.Token);

                    source.AdvanceTo(readResult.Buffer.GetPosition(nextBlockOffset));
                    id++;
                }

                inputChannel.Writer.Complete();
            });

            Task[] inputConsumerOutputProducers = StartDecompressBlock(dictionary, maxBlockSize, threadCount, inputChannel, outputChannel, channelToken);
            Task outputConsumer = StartWriteDecompressedBuffer(destination, outputChannel, channelToken);

            try
            {
                await inputProducer;
                await Task.WhenAll(inputConsumerOutputProducers);
                outputChannel.Writer.Complete();
                await outputConsumer;
            }
            catch
            {
                channelToken.Cancel(); // when any exception, cancel all tasks.
                throw;
            }
        }
    }

    static Task StartWriteDecompressedBuffer(PipeWriter destination, Channel<DecompressionOutputBuffer> outputChannel, CancellationTokenSource channelToken)
    {
        var outputConsumer = Task.Run(async () =>
        {
            var reader = outputChannel.Reader;
            var nextId = 0; // id for write
            var buffers = new MiniPriorityQueue<DecompressionOutputBuffer>();

            try
            {
                while (await reader.WaitToReadAsync(channelToken.Token))
                {
                    while (reader.TryRead(out var item))
                    {
                        buffers.Enqueue(item);

                        while (buffers.Count > 0 && buffers.Peek().Id == nextId)
                        {
                            var source = buffers.Dequeue();
                            nextId++;

                            await destination.WriteAsync(source.DecompressedBuffer.AsMemory(0, source.Count), channelToken.Token);
                            ArrayPool<byte>.Shared.Return(source.DecompressedBuffer, clearArray: false);
                        }
                    }
                }

                await destination.FlushAsync(channelToken.Token);
            }
            finally
            {
                // if buffer is remained, return to pool.
                foreach (var item in buffers.Values)
                {
                    ArrayPool<byte>.Shared.Return(item.DecompressedBuffer, clearArray: false);
                }
            }
        });
        return outputConsumer;
    }

    static Task[] StartDecompressBlock(LZ4CompressionDictionary? dictionary, int maxBlockSize, int threadCount, Channel<DecompressionInputBuffer> inputChannel, Channel<DecompressionOutputBuffer> outputChannel, CancellationTokenSource channelToken)
    {
        var inputConsumerOutputProducers = new Task[threadCount];
        for (var i = 0; i < inputConsumerOutputProducers.Length; i++)
        {
            inputConsumerOutputProducers[i] = Task.Run(async () =>
            {
                while (await inputChannel.Reader.WaitToReadAsync(channelToken.Token))
                {
                    while (inputChannel.Reader.TryRead(out var item))
                    {
                        var destination = ArrayPool<byte>.Shared.Rent(maxBlockSize);
                        int written;
                        if (item.IsUncompressed)
                        {
                            item.CompressedBuffer.CopyTo(destination);
                            written = item.CompressedBuffer.Length;
                        }
                        else
                        {
                            // use LZ4 raw block decompress
                            written = LZ4.Block.Decompress(item.CompressedBuffer.Span, destination, dictionary);
                        }

                        if (item.IsBufferRentFromPool && MemoryMarshal.TryGetArray(item.CompressedBuffer, out var segment))
                        {
                            ArrayPool<byte>.Shared.Return(segment.Array!, clearArray: false);
                        }

                        var item2 = new DecompressionOutputBuffer
                        {
                            Id = item.Id,
                            DecompressedBuffer = destination,
                            Count = written
                        };

                        await outputChannel.Writer.WriteAsync(item2, channelToken.Token);
                    }
                }
            });
        }

        return inputConsumerOutputProducers;
    }

    static BlockHeader ReadBlockHeader(ReadOnlySpan<byte> source)
    {
        // need 4 bytes
        if (source.Length < 4) Throws.ArgumentOutOfRangeException(nameof(source));

        var header = BinaryPrimitives.ReadUInt32LittleEndian(source);
        return new BlockHeader(header);
    }

    static BlockHeader ReadBlockHeader(ReadOnlySequence<byte> source)
    {
        // need 4 bytes
        if (source.Length < 4) Throws.ArgumentOutOfRangeException(nameof(source));

        if (source.FirstSpan.Length >= 4)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(source.FirstSpan);
            return new BlockHeader(header);
        }
        else
        {
            Span<byte> temp = stackalloc byte[4];
            source.Slice(0, 4).CopyTo(temp);
            var header = BinaryPrimitives.ReadUInt32LittleEndian(temp);
            return new BlockHeader(header);
        }
    }

    static async ValueTask<BlockHeader> ReadBlockHeaderAsync(PipeReader source, CancellationToken cancellationToken)
    {
        var readResult = await source.ReadAtLeastAsync(4, cancellationToken);

        uint header;
        if (readResult.Buffer.FirstSpan.Length >= 4)
        {
            header = BinaryPrimitives.ReadUInt32LittleEndian(readResult.Buffer.FirstSpan);
        }
        else
        {
            Span<byte> buffer = stackalloc byte[4];
            readResult.Buffer.Slice(0, 4).CopyTo(buffer);
            header = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        }

        source.AdvanceTo(readResult.Buffer.GetPosition(4));

        return new BlockHeader(header);
    }

    struct BlockHeader(uint flag)
    {
        const uint LZ4_BLOCK_UNCOMPRESSED_FLAG = 0x80000000;
        const uint LZ4_BLOCK_SIZE_MASK = 0x7FFFFFFF;

        public bool IsEndMark => flag == 0;
        public bool IsUncompressed => (flag & LZ4_BLOCK_UNCOMPRESSED_FLAG) != 0;
        public int CompressedSize => (int)(flag & LZ4_BLOCK_SIZE_MASK);
    }

    [StructLayout(LayoutKind.Auto)]
    struct DecompressionInputBuffer : IComparable<DecompressionInputBuffer>
    {
        public int Id;
        public bool IsUncompressed;
        public bool IsBufferRentFromPool;
        public ReadOnlyMemory<byte> CompressedBuffer; // if IsUncomperred, this is Uncompressed buffer.

        public int CompareTo(DecompressionInputBuffer other)
        {
            return Id.CompareTo(other.Id);
        }

        public override string ToString()
        {
            return Id.ToString();
        }
    }

    [StructLayout(LayoutKind.Auto)]
    struct DecompressionOutputBuffer : IComparable<DecompressionOutputBuffer>
    {
        public int Id;
        public byte[] DecompressedBuffer;
        public int Count;

        public int CompareTo(DecompressionOutputBuffer other)
        {
            return Id.CompareTo(other.Id);
        }

        public override string ToString()
        {
            return Id.ToString();
        }
    }
}
