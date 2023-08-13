using System.Buffers;

namespace NativeCompressions.ZStandard
{
    // C# 12, we can use InlineArray.
    // https://github.com/dotnet/runtime/pull/90459
    // TODO: internal
    public struct ArraySequence : IDisposable
    {
        public int index;
        public int length;

        ArrayBlock block0;
        ArrayBlock block1;
        ArrayBlock block2;
        ArrayBlock block3;
        ArrayBlock block4;
        ArrayBlock block5;
        ArrayBlock block6;
        ArrayBlock block7;
        ArrayBlock block8;
        ArrayBlock block9;
        ArrayBlock block10;
        ArrayBlock block11;

        static ref ArrayBlock GetBlock(ref ArraySequence self, int index)
        {
            switch (index)
            {
                case 0: return ref self.block0;
                case 1: return ref self.block1;
                case 2: return ref self.block2;
                case 3: return ref self.block3;
                case 4: return ref self.block4;
                case 5: return ref self.block5;
                case 6: return ref self.block6;
                case 7: return ref self.block7;
                case 8: return ref self.block8;
                case 9: return ref self.block9;
                case 10: return ref self.block10;
                case 11: return ref self.block11;
                default:
                    throw new InvalidOperationException();
            }
        }

        public ArraySequence(int initialSize)
        {
            index = 0;
            length = 0;
            block0 = block1 = block2 = block3 = block4 = block5 = block6 = block7 = block8 = block9 = block10 = block11 = default;
            block0 = ArrayBlock.Create(Math.Min(initialSize, ushort.MaxValue));
        }

        public Span<byte> CurrentSpan
        {
            get
            {
                ref var block = ref GetBlock(ref this, index);
                return block.Block.AsSpan(block.Count);
            }
        }

        // flush current block and get next.
        public Span<byte> AllocateNextBlock(int flushCount)
        {
            ref var current = ref GetBlock(ref this, index);
            ref var next = ref GetBlock(ref this, index + 1);

            current.Count = flushCount;
            var nextSize = unchecked(current.Block.Length * 2);
            nextSize = (nextSize < 0) ? Array.MaxLength : nextSize;
            next = ArrayBlock.Create(nextSize);

            index++;
            length = checked(length + flushCount); // does not allow overflow
            return next.Block;
        }

        public byte[] ToArrayAndDispose(int lastBlockCount)
        {
            if (index == -1) throw new ObjectDisposedException(nameof(ArraySequence));
            if (length == 0) return Array.Empty<byte>();

            var result = GC.AllocateUninitializedArray<byte>(length);

            var dest = result.AsSpan();

            // Concatenate blocks
            for (int i = 0; i < index; i++)
            {
                ref var block = ref GetBlock(ref this, i);
                block.Block.AsSpan(0, block.Count).CopyTo(dest);
                block.ReturnBlock();
                dest = dest.Slice(block.Count);
            }

            // Flush final block
            {
                ref var block = ref GetBlock(ref this, index);
                block.Block.AsSpan(0, lastBlockCount).CopyTo(dest);
                block.ReturnBlock();
            }

            index = -1;
            return result;
        }

        public void Dispose()
        {
            if (index == -1) return;

            for (int i = 0; i <= index; i++)
            {
                GetBlock(ref this, i).ReturnBlock();
            }
            index = -1;
        }

        struct ArrayBlock
        {
            public int Count;
            public byte[] Block;

            public static ArrayBlock Create(int size) => new ArrayBlock { Block = ArrayPool<byte>.Shared.Rent(size) };

            public void ReturnBlock()
            {
                ArrayPool<byte>.Shared.Return(Block);
                Block = null!;
            }
        }
    }
}
