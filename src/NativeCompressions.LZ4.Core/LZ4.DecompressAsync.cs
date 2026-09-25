using Microsoft.Win32.SafeHandles;
using NativeCompressions.Internal;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace NativeCompressions;

public static partial class LZ4
{
    // Every DecompressAsync overload turns its source into a PipeReader and runs DecompressCoreAsync,
    // so they share one behavior: concatenated frames (and skippable frames) are all decoded, the same
    // as LZ4Stream and the one-shot Decompress. Invalid data and input that ends inside a frame throw LZ4Exception.
    // Frames in BlockIndependent mode are decoded block-parallel when maxDegreeOfParallelism is 2 or more,
    // with block and content checksums verified the same way LZ4F does.

    static readonly StreamPipeReaderOptions LargeBufferLeaveOpenPipeReaderOptions = new StreamPipeReaderOptions(bufferSize: 65536, leaveOpen: true);

    public static async ValueTask DecompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, LZ4DecompressionOptions? options = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        var reader = PipeReader.Create(new ReadOnlySequence<byte>(source));
        await DecompressCoreAsync(reader, destination, options ?? LZ4DecompressionOptions.Default, maxDegreeOfParallelism, cancellationToken);
        await reader.CompleteAsync();
    }

    public static async ValueTask DecompressAsync(ReadOnlySequence<byte> source, PipeWriter destination, LZ4DecompressionOptions? options = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        var reader = PipeReader.Create(source);
        await DecompressCoreAsync(reader, destination, options ?? LZ4DecompressionOptions.Default, maxDegreeOfParallelism, cancellationToken);
        await reader.CompleteAsync();
    }

    public static ValueTask DecompressAsync(SafeFileHandle source, PipeWriter destination, LZ4DecompressionOptions? options = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        return DecompressAsync(source, 0, destination, options, maxDegreeOfParallelism, cancellationToken);
    }

    public static async ValueTask DecompressAsync(SafeFileHandle source, long offset, PipeWriter destination, LZ4DecompressionOptions? options = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
#if NETSTANDARD
        var stream = NonOwningFileStream.Open(source, offset); // not disposed, it does not own the handle
#else
        var stream = new RandomAccessReadStream(source, offset);
#endif
        var reader = PipeReader.Create(stream, LargeBufferLeaveOpenPipeReaderOptions);
        await DecompressCoreAsync(reader, destination, options ?? LZ4DecompressionOptions.Default, maxDegreeOfParallelism, cancellationToken);
        await reader.CompleteAsync();
    }

    public static async ValueTask DecompressAsync(Stream source, PipeWriter destination, LZ4DecompressionOptions? options = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        if (source is MemoryStream ms && ms.TryGetBuffer(out var buffer))
        {
            // honor the stream position, and leave the stream at the end like a normal read would.
            // A position at or past the end is a legal EOF and is left where it is.
            if (ms.Position >= ms.Length)
            {
                await DecompressAsync(ReadOnlyMemory<byte>.Empty, destination, options, maxDegreeOfParallelism, cancellationToken);
                return;
            }
            var position = (int)ms.Position;
            await DecompressAsync(((ReadOnlyMemory<byte>)buffer).Slice(position), destination, options, maxDegreeOfParallelism, cancellationToken);
            ms.Position = ms.Length;
            return;
        }

#if !NETSTANDARD
        if (source is FileStream fs && fs.CanSeek)
        {
            await DecompressAsync(fs.SafeFileHandle, fs.Position, destination, options, maxDegreeOfParallelism, cancellationToken);
            return;
        }
#endif

        var reader = PipeReader.Create(source, LeaveOpenPipeReaderOptions);
        await DecompressCoreAsync(reader, destination, options ?? LZ4DecompressionOptions.Default, maxDegreeOfParallelism, cancellationToken);
        await reader.CompleteAsync();
    }

    public static ValueTask DecompressAsync(PipeReader source, PipeWriter destination, LZ4DecompressionOptions? options = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        return DecompressCoreAsync(source, destination, options ?? LZ4DecompressionOptions.Default, maxDegreeOfParallelism, cancellationToken);
    }

    public static async ValueTask DecompressAsync(string sourceFilePath, string destinationFilePath, LZ4DecompressionOptions? options = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        using var sourceHandle = File.OpenHandle(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        using var destinationStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.Asynchronous);
        var destinationWriter = PipeWriter.Create(destinationStream);
        await DecompressAsync(sourceHandle, destinationWriter, options, maxDegreeOfParallelism, cancellationToken);
    }

    public static async ValueTask DecompressAsync(string sourceFilePath, PipeWriter destination, LZ4DecompressionOptions? options = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        using var sourceHandle = File.OpenHandle(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        await DecompressAsync(sourceHandle, destination, options, maxDegreeOfParallelism, cancellationToken);
    }

    // ---- core

    static async ValueTask DecompressCoreAsync(PipeReader source, PipeWriter destination, LZ4DecompressionOptions options, int? maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        using var decoder = new LZ4Decoder(options);

        while (true)
        {
            // frame header, or the end of input
            var result = await source.ReadAtLeastAsync(MinSizeToKnowFrameHeaderLength, cancellationToken);
            if (result.IsCanceled) throw new OperationCanceledException();

            var buffer = result.Buffer;
            if (buffer.IsEmpty)
            {
                source.AdvanceTo(buffer.End);
                return; // no more frames
            }
            if (buffer.Length < MinSizeToKnowFrameHeaderLength)
            {
                throw new LZ4Exception("Invalid LZ4 frame: input ends inside a frame header.");
            }

            var headerSize = ReadHeaderSize(buffer, decoder); // throws on bad magic
            source.AdvanceTo(buffer.Start); // consume nothing, and leave the body unexamined so later reads return it at once

            result = await source.ReadAtLeastAsync(headerSize, cancellationToken);
            if (result.IsCanceled) throw new OperationCanceledException();
            buffer = result.Buffer;
            if (buffer.Length < headerSize)
            {
                throw new LZ4Exception("Invalid LZ4 frame: input ends inside a frame header.");
            }

            var frameInfo = ReadFrameInfo(buffer, headerSize, decoder, out var consumed);
            source.AdvanceTo(buffer.GetPosition(consumed));

            var parallel = maxDegreeOfParallelism > 1
                && frameInfo.FrameType == FrameType.Frame
                && frameInfo.BlockMode == BlockMode.BlockIndependent;

            if (parallel)
            {
                await DecompressBlocksParallelAsync(source, destination, frameInfo, options, maxDegreeOfParallelism!.Value, cancellationToken);
                decoder.Reset(); // the decoder only saw the header
            }
            else
            {
                var maxBlockSize = frameInfo.FrameType == FrameType.Frame ? GetMaxBlockSize(frameInfo.BlockSizeID) : 4096;
                await DecompressFrameAsync(decoder, source, destination, maxBlockSize, cancellationToken);
            }
        }
    }

    static int ReadHeaderSize(in ReadOnlySequence<byte> buffer, LZ4Decoder decoder)
    {
        Span<byte> header = stackalloc byte[MinSizeToKnowFrameHeaderLength];
        buffer.Slice(0, MinSizeToKnowFrameHeaderLength).CopyTo(header);
        return decoder.GetHeaderSize(header);
    }

    static LZ4FrameInfo ReadFrameInfo(in ReadOnlySequence<byte> buffer, int headerSize, LZ4Decoder decoder, out int consumed)
    {
        Span<byte> header = stackalloc byte[MaxFrameHeaderLength];
        buffer.Slice(0, headerSize).CopyTo(header);
        return decoder.GetFrameInfo(header.Slice(0, headerSize), out consumed);
    }

    // Decodes one frame with the streaming decoder. Leaves the input after the frame so the next frame can follow.
    static async ValueTask DecompressFrameAsync(LZ4Decoder decoder, PipeReader source, PipeWriter destination, int maxBlockSize, CancellationToken cancellationToken)
    {
        var status = OperationStatus.NeedMoreData;
        var pending = 0; // bytes advanced but not yet flushed

        while (true)
        {
            var result = await source.ReadAsync(cancellationToken);
            if (result.IsCanceled) throw new OperationCanceledException();

            var buffer = result.Buffer;
            long consumedInBuffer = 0;
            foreach (var segment in buffer)
            {
                var src = segment;
                while (src.Length > 0)
                {
                    var dest = destination.GetMemory(maxBlockSize);
                    status = decoder.Decompress(src.Span, dest.Span, out var bytesConsumed, out var bytesWritten);
                    destination.Advance(bytesWritten);
                    pending += bytesWritten;
                    src = src.Slice(bytesConsumed);
                    consumedInBuffer += bytesConsumed;

                    if (status == OperationStatus.InvalidData)
                    {
                        throw new LZ4Exception("Invalid LZ4 frame.");
                    }

                    if (pending >= maxBlockSize)
                    {
                        await destination.FlushAsync(cancellationToken);
                        pending = 0;
                    }

                    if (status == OperationStatus.Done)
                    {
                        source.AdvanceTo(buffer.GetPosition(consumedInBuffer));
                        if (pending > 0) await destination.FlushAsync(cancellationToken);
                        return;
                    }
                }
            }
            source.AdvanceTo(buffer.End);

            if (result.IsCompleted)
            {
                break;
            }
        }

        // input is exhausted, write out what the decoder still holds
        while (status == OperationStatus.DestinationTooSmall)
        {
            var dest = destination.GetMemory(maxBlockSize);
            status = decoder.Decompress(ReadOnlySpan<byte>.Empty, dest.Span, out _, out var bytesWritten);
            destination.Advance(bytesWritten);
            pending += bytesWritten;
            if (bytesWritten == 0 && status == OperationStatus.DestinationTooSmall)
            {
                throw new LZ4Exception("Invalid LZ4 frame: decoder made no progress.");
            }
        }
        if (pending > 0) await destination.FlushAsync(cancellationToken);

        if (status != OperationStatus.Done)
        {
            throw new LZ4Exception("Invalid LZ4 frame: input ends inside a frame.");
        }
    }

    // Block-parallel decode of one BlockIndependent frame. The input is positioned right after the frame header
    // and is left right after the frame (end mark and content checksum consumed).
    // Block checksums are verified by the workers, the content checksum by the ordered writer, unless SkipChecksums is set.
    static async ValueTask DecompressBlocksParallelAsync(PipeReader source, PipeWriter destination, LZ4FrameInfo frameInfo, LZ4DecompressionOptions options, int threadCount, CancellationToken cancellationToken)
    {
        // a block never exceeds the frame's block size: data that would not compress is stored raw instead
        var maxBlockSize = GetMaxBlockSize(frameInfo.BlockSizeID);
        var maxCompressedBlockSize = maxBlockSize;
        var verifyBlockChecksum = frameInfo.BlockChecksumFlag == BlockChecksum.BlockChecksumEnabled && !options.SkipChecksums;
        var verifyContentChecksum = frameInfo.ContentChecksumFlag == ContentChecksum.ContentChecksumEnabled && !options.SkipChecksums;
        var checksumSize = frameInfo.BlockChecksumFlag == BlockChecksum.BlockChecksumEnabled ? 4 : 0;
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

        // reads blocks and hands them to the workers, returns the content checksum stored in the frame footer
        var inputProducer = Task.Run(async () =>
        {
            try
            {
                var id = 0;
                while (true)
                {
                    var blockHeader = await ReadBlockHeaderAsync(source, channelToken.Token);
                    if (blockHeader.IsEndMark) break;

                    if (blockHeader.CompressedSize > maxCompressedBlockSize)
                    {
                        throw new LZ4Exception("Invalid LZ4 frame: block size exceeds the frame's block size limit.");
                    }

                    var blockLength = blockHeader.CompressedSize + checksumSize;
                    var readResult = await source.ReadAtLeastAsync(blockLength, channelToken.Token);
                    if (readResult.Buffer.Length < blockLength)
                    {
                        throw new LZ4Exception("Invalid LZ4 frame: input ends inside a block.");
                    }

                    var compressedBuffer = ArrayPool<byte>.Shared.Rent(blockHeader.CompressedSize);
                    readResult.Buffer.Slice(0, blockHeader.CompressedSize).CopyTo(compressedBuffer);

                    uint blockChecksum = 0;
                    if (checksumSize != 0)
                    {
                        blockChecksum = ReadUInt32(readResult.Buffer.Slice(blockHeader.CompressedSize, 4));
                    }
                    source.AdvanceTo(readResult.Buffer.GetPosition(blockLength));

                    var item = new DecompressionInputBuffer
                    {
                        Id = id,
                        IsUncompressed = blockHeader.IsUncompressed,
                        CompressedBuffer = compressedBuffer.AsMemory(0, blockHeader.CompressedSize),
                        IsBufferRentFromPool = true,
                        VerifyBlockChecksum = verifyBlockChecksum,
                        BlockChecksum = blockChecksum,
                    };
                    await inputChannel.Writer.WriteAsync(item, channelToken.Token);
                    id++;
                }

                uint? contentChecksum = null;
                if (frameInfo.ContentChecksumFlag == ContentChecksum.ContentChecksumEnabled)
                {
                    var readResult = await source.ReadAtLeastAsync(4, channelToken.Token);
                    if (readResult.Buffer.Length < 4)
                    {
                        throw new LZ4Exception("Invalid LZ4 frame: input ends inside the content checksum.");
                    }
                    contentChecksum = ReadUInt32(readResult.Buffer.Slice(0, 4));
                    source.AdvanceTo(readResult.Buffer.GetPosition(4));
                }

                inputChannel.Writer.Complete();
                return contentChecksum;
            }
            catch (Exception ex)
            {
                inputChannel.Writer.TryComplete(ex);
                throw;
            }
        });

        // workers complete the output channel when they are done, or fail it so the writer stops
        var failure = new FirstFailure();
        var workers = Task.Run(async () =>
        {
            try
            {
                await StartDecompressBlock(options.Dictionary, maxBlockSize, threadCount, inputChannel, outputChannel, channelToken, failure);
                outputChannel.Writer.Complete();
            }
            catch (Exception ex)
            {
                outputChannel.Writer.TryComplete(ex);
                throw;
            }
        });
        var outputConsumer = StartWriteDecompressedBuffer(destination, outputChannel, verifyContentChecksum, channelToken);

        // The producer blocks on a full channel when the workers die, so the first failure must cancel the others
        // before anything is awaited to completion.
        try
        {
            await WhenAllCancelOnFailureAsync(channelToken, inputProducer, workers, outputConsumer);
        }
        catch (OperationCanceledException) when (failure.Exception != null)
        {
            // a worker failed first and cancelled the rest, report its exception rather than the cancellation
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.Exception).Throw();
        }

        var (contentChecksum, totalWritten) = outputConsumer.Result;
        if (verifyContentChecksum && inputProducer.Result != contentChecksum)
        {
            throw new LZ4Exception("Invalid LZ4 frame: content checksum mismatch.");
        }

        // LZ4F validates the recorded content size at the end of a frame, and so must the block parallel path
        if (frameInfo.ContentSize != 0 && frameInfo.ContentSize != (ulong)totalWritten)
        {
            throw new LZ4Exception($"Invalid LZ4 frame: content size mismatch. Header records {frameInfo.ContentSize} bytes, frame decoded to {totalWritten} bytes.");
        }
    }

    // Waits for every task. When one faults, the token is cancelled so the others stop, and the first real failure is rethrown.
    static async Task WhenAllCancelOnFailureAsync(CancellationTokenSource cancellation, params Task[] tasks)
    {
        var pending = new List<Task>(tasks);
        Exception? failure = null;
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);

            if (completed.IsFaulted || completed.IsCanceled)
            {
                // a parallel loop aggregates the cancellations that followed the real failure, so look past those
                var ex = completed.Exception?.Flatten().InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException)
                    ?? completed.Exception?.InnerException
                    ?? new OperationCanceledException();
                if (failure == null || (failure is OperationCanceledException && ex is not OperationCanceledException))
                {
                    failure = ex;
                }
                cancellation.Cancel();
            }
        }

        if (failure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    // writes decoded blocks in order and returns the xxHash32 of the content (when asked to compute it) and the total size
    static Task<(uint ContentChecksum, long TotalWritten)> StartWriteDecompressedBuffer(PipeWriter destination, Channel<DecompressionOutputBuffer> outputChannel, bool computeContentChecksum, CancellationTokenSource channelToken)
    {
        return Task.Run(async () =>
        {
            var reader = outputChannel.Reader;
            var nextId = 0; // id for write
            var buffers = new MiniPriorityQueue<DecompressionOutputBuffer>();
            var hash = new XxHash32();
            long totalWritten = 0;

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

                            if (computeContentChecksum)
                            {
                                hash.Update(source.DecompressedBuffer.AsSpan(0, source.Count));
                            }
                            totalWritten += source.Count;
                            await destination.WriteAsync(source.DecompressedBuffer.AsMemory(0, source.Count), channelToken.Token);
                            ArrayPool<byte>.Shared.Return(source.DecompressedBuffer, clearArray: false);
                        }
                    }
                }

                await destination.FlushAsync(channelToken.Token);
                return (hash.Digest(), totalWritten);
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
    }

    static Task StartDecompressBlock(LZ4Dictionary? dictionary, int maxBlockSize, int threadCount, Channel<DecompressionInputBuffer> inputChannel, Channel<DecompressionOutputBuffer> outputChannel, CancellationTokenSource channelToken, FirstFailure failure)
    {
        Task inputConsumerOutputProducers;
#if NET8_0_OR_GREATER
        inputConsumerOutputProducers = Parallel.ForAsync(0, threadCount, async (i, _) =>
        {
            await DecompressBlocksAsync(dictionary, maxBlockSize, inputChannel, outputChannel, channelToken, failure);
        });
#else
        var inputConsumerOutputProducerTasks = new Task[threadCount];
        for (var i = 0; i < inputConsumerOutputProducerTasks.Length; i++)
        {
            inputConsumerOutputProducerTasks[i] = Task.Run(() => DecompressBlocksAsync(dictionary, maxBlockSize, inputChannel, outputChannel, channelToken, failure));
        }

        inputConsumerOutputProducers = Task.WhenAll(inputConsumerOutputProducerTasks);
#endif

        return inputConsumerOutputProducers;
    }

    static async Task DecompressBlocksAsync(LZ4Dictionary? dictionary, int maxBlockSize, Channel<DecompressionInputBuffer> inputChannel, Channel<DecompressionOutputBuffer> outputChannel, CancellationTokenSource channelToken, FirstFailure failure)
    {
        try
        {
            await DecompressBlocksCoreAsync(dictionary, maxBlockSize, inputChannel, outputChannel, channelToken);
        }
        catch (Exception ex)
        {
            // The other workers and the producer wait on the channels and the input, which may never deliver more.
            // The first failure has to stop them, or the whole operation hangs until the caller cancels.
            // Cancelling wakes the others synchronously, so their cancellations can be recorded before this
            // exception; remember it here so the caller can report the real cause.
            if (ex is not OperationCanceledException) failure.Record(ex);
            channelToken.Cancel();
            throw;
        }
    }

    sealed class FirstFailure
    {
        Exception? exception;

        public Exception? Exception => exception;

        public void Record(Exception ex) => Interlocked.CompareExchange(ref exception, ex, null);
    }

    static async Task DecompressBlocksCoreAsync(LZ4Dictionary? dictionary, int maxBlockSize, Channel<DecompressionInputBuffer> inputChannel, Channel<DecompressionOutputBuffer> outputChannel, CancellationTokenSource channelToken)
    {
        while (await inputChannel.Reader.WaitToReadAsync(channelToken.Token))
        {
            while (inputChannel.Reader.TryRead(out var item))
            {
                var destination = ArrayPool<byte>.Shared.Rent(maxBlockSize);
                int written;
                try
                {
                    // the block checksum covers the block as stored, compressed or not
                    if (item.VerifyBlockChecksum && XxHash32.Hash(item.CompressedBuffer.Span) != item.BlockChecksum)
                    {
                        throw new LZ4Exception("Invalid LZ4 frame: block checksum mismatch.");
                    }

                    if (item.IsUncompressed)
                    {
                        item.CompressedBuffer.CopyTo(destination);
                        written = item.CompressedBuffer.Length;
                    }
                    else
                    {
                        // use LZ4 raw block decompress
                        written = Block.Decompress(item.CompressedBuffer.Span, destination, dictionary);
                    }
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(destination, clearArray: false);
                    throw;
                }
                finally
                {
                    if (item.IsBufferRentFromPool && MemoryMarshal.TryGetArray(item.CompressedBuffer, out var segment))
                    {
                        ArrayPool<byte>.Shared.Return(segment.Array!, clearArray: false);
                    }
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
    }

    static async ValueTask<BlockHeader> ReadBlockHeaderAsync(PipeReader source, CancellationToken cancellationToken)
    {
        var readResult = await source.ReadAtLeastAsync(4, cancellationToken);
        if (readResult.Buffer.Length < 4)
        {
            throw new LZ4Exception("Invalid LZ4 frame: input ends inside a block header.");
        }

        var header = ReadUInt32(readResult.Buffer.Slice(0, 4));
        source.AdvanceTo(readResult.Buffer.GetPosition(4));

        return new BlockHeader(header);
    }

    static uint ReadUInt32(ReadOnlySequence<byte> fourBytes)
    {
        if (fourBytes.FirstSpan.Length >= 4)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(fourBytes.FirstSpan);
        }

        Span<byte> buffer = stackalloc byte[4];
        fourBytes.CopyTo(buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    readonly struct BlockHeader(uint flag)
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
        public bool VerifyBlockChecksum;
        public uint BlockChecksum;
        public ReadOnlyMemory<byte> CompressedBuffer; // if IsUncompressed, this is the raw block.

        public int CompareTo(DecompressionInputBuffer other) => Id.CompareTo(other.Id);

        public override string ToString() => Id.ToString();
    }

    [StructLayout(LayoutKind.Auto)]
    struct DecompressionOutputBuffer : IComparable<DecompressionOutputBuffer>
    {
        public int Id;
        public byte[] DecompressedBuffer;
        public int Count;

        public int CompareTo(DecompressionOutputBuffer other) => Id.CompareTo(other.Id);

        public override string ToString() => Id.ToString();
    }
}
