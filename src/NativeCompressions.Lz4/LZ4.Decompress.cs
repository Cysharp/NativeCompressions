using NativeCompressions.LZ4.Internal;
using NativeCompressions.LZ4.Raw;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net.NetworkInformation;
using System.Numerics;
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
            Span<byte> scratch = stackalloc byte[127];
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

    public static async ValueTask DecompressAsync(ReadOnlyMemory<byte> source, PipeWriter destination, LZ4FrameOptions options, LZ4CompressionDictionary? dictionary = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        using var decoder = new LZ4Decoder(dictionary);


        var frameInfo = decoder.GetFrameInfo(source.Span, out var bytesConsumed);
        source = source.Slice(bytesConsumed);


        // get block header...
        if (frameInfo.BlockMode == BlockMode.BlockIndependent)
        {
            var checksumSize = (frameInfo.BlockChecksumFlag == BlockChecksum.BlockChecksumEnabled)
                ? 4
                : 0;

            var maxBlockSize = GetMaxBlockSize(frameInfo.BlockSizeID);

            var threadCount = maxDegreeOfParallelism ?? Environment.ProcessorCount;
            var capacity = Math.Min(threadCount * 2, 8);

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
                        CompressedBuffer = source.Slice(4, nextBlockOffset - checksumSize) // skip block header, checksum
                    };

                    await inputChannel.Writer.WriteAsync(item, cancellationToken);

                    source = source.Slice(4 + nextBlockOffset);
                    id++;
                }
                inputChannel.Writer.Complete();
            });

            threadCount = 1; // debug
            var inputConsumerOutputProducers = new Task[threadCount];
            for (var i = 0; i < inputConsumerOutputProducers.Length; i++)
            {
                inputConsumerOutputProducers[i] = Task.Run(async () =>
                {
                    using var decoder = new LZ4Decoder(dictionary);

                    while (await inputChannel.Reader.WaitToReadAsync(cancellationToken))
                    {
                        while (inputChannel.Reader.TryRead(out var item))
                        {
                            var destination = ArrayPool<byte>.Shared.Rent(maxBlockSize);
                            try
                            {
                                // use raw block decompress
                                var written = LZ4.Block.Decompress(item.CompressedBuffer.Span, destination, dictionary);

                                var item2 = new DecompressionOutputBuffer
                                {
                                    Id = item.Id,
                                    DecompressedBuffer = destination,
                                    Count = written
                                };

                                await outputChannel.Writer.WriteAsync(item2, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex);
                            }
                        }
                    }
                });
            }

            var outputConsumer = Task.Run(async () =>
            {
                var reader = outputChannel.Reader;
                var nextId = 0; // id for write
                var buffers = new List<DecompressionOutputBuffer>();

                while (await reader.WaitToReadAsync(cancellationToken))
                {
                    while (reader.TryRead(out var item))
                    {
                        buffers.Add(item);
                        buffers.Sort(); // reverse-order

                        while (buffers.Count > 0 && buffers[^1].Id == nextId)
                        {
                            var source = buffers[^1];
                            await destination.WriteAsync(source.DecompressedBuffer.AsMemory(0, source.Count), cancellationToken);
                            buffers.RemoveAt(buffers.Count - 1); // Remove from last is performant than remove first
                            ArrayPool<byte>.Shared.Return(source.DecompressedBuffer, clearArray: false);
                            nextId++;
                        }
                    }
                }
            });

            await inputProducer;
            await Task.WhenAll(inputConsumerOutputProducers);
            outputChannel.Writer.Complete();
            await outputConsumer;
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

    // need 4 bytes
    static BlockHeader ReadBlockHeader(ReadOnlySpan<byte> source)
    {
        var header = BinaryPrimitives.ReadUInt32LittleEndian(source);
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

    // use PriorityQueue is better but to support netstandard2.1
    internal struct DecompressionInputBuffer : IComparable<DecompressionInputBuffer>
    {
        public int Id;
        public ReadOnlyMemory<byte> CompressedBuffer;

        public int CompareTo(DecompressionInputBuffer other)
        {
            // reverse-order
            if (Id < other.Id) return 1;
            if (Id > other.Id) return -1;
            return 0;
        }
    }

    internal struct DecompressionOutputBuffer : IComparable<DecompressionInputBuffer>
    {
        public int Id;
        public byte[] DecompressedBuffer;
        public int Count;

        public int CompareTo(DecompressionInputBuffer other)
        {
            // reverse-order
            if (Id < other.Id) return 1;
            if (Id > other.Id) return -1;
            return 0;
        }
    }
}
