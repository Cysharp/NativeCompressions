using System.Buffers.Binary;

namespace NativeCompressions.Internal;

// xxHash32 as used by the LZ4 frame format for block and content checksums (seed 0).
// Managed because the bundled lz4 binaries do not export their internal xxHash symbols.
internal sealed class XxHash32
{
    const uint Prime1 = 2654435761U;
    const uint Prime2 = 2246822519U;
    const uint Prime3 = 3266489917U;
    const uint Prime4 = 668265263U;
    const uint Prime5 = 374761393U;

    readonly byte[] buffer = new byte[16];
    int buffered;
    ulong totalLength;
    uint v1, v2, v3, v4;
    uint seed;

    public XxHash32(uint seed = 0)
    {
        Reset(seed);
    }

    public void Reset(uint seed = 0)
    {
        this.seed = seed;
        v1 = seed + Prime1 + Prime2;
        v2 = seed + Prime2;
        v3 = seed;
        v4 = seed - Prime1;
        buffered = 0;
        totalLength = 0;
    }

    public void Update(ReadOnlySpan<byte> input)
    {
        totalLength += (ulong)input.Length;

        if (buffered + input.Length < 16)
        {
            input.CopyTo(buffer.AsSpan(buffered));
            buffered += input.Length;
            return;
        }

        if (buffered > 0)
        {
            var fill = 16 - buffered;
            input.Slice(0, fill).CopyTo(buffer.AsSpan(buffered));
            Stripe(buffer);
            input = input.Slice(fill);
            buffered = 0;
        }

        while (input.Length >= 16)
        {
            Stripe(input);
            input = input.Slice(16);
        }

        if (input.Length > 0)
        {
            input.CopyTo(buffer);
            buffered = input.Length;
        }
    }

    public uint Digest()
    {
        var h = totalLength >= 16
            ? Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18)
            : seed + Prime5;
        h += (uint)totalLength;
        return Finalize(h, buffer.AsSpan(0, buffered));
    }

    /// <summary>
    /// Hashes a complete buffer without allocating state.
    /// </summary>
    public static uint Hash(ReadOnlySpan<byte> input, uint seed = 0)
    {
        uint h;
        var length = (uint)input.Length;

        if (input.Length >= 16)
        {
            var a = seed + Prime1 + Prime2;
            var b = seed + Prime2;
            var c = seed;
            var d = seed - Prime1;
            while (input.Length >= 16)
            {
                a = Round(a, BinaryPrimitives.ReadUInt32LittleEndian(input));
                b = Round(b, BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(4)));
                c = Round(c, BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(8)));
                d = Round(d, BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(12)));
                input = input.Slice(16);
            }
            h = Rotl(a, 1) + Rotl(b, 7) + Rotl(c, 12) + Rotl(d, 18);
        }
        else
        {
            h = seed + Prime5;
        }

        h += length;
        return Finalize(h, input);
    }

    void Stripe(ReadOnlySpan<byte> sixteenBytes)
    {
        v1 = Round(v1, BinaryPrimitives.ReadUInt32LittleEndian(sixteenBytes));
        v2 = Round(v2, BinaryPrimitives.ReadUInt32LittleEndian(sixteenBytes.Slice(4)));
        v3 = Round(v3, BinaryPrimitives.ReadUInt32LittleEndian(sixteenBytes.Slice(8)));
        v4 = Round(v4, BinaryPrimitives.ReadUInt32LittleEndian(sixteenBytes.Slice(12)));
    }

    static uint Rotl(uint x, int r) => (x << r) | (x >> (32 - r)); // BitOperations is not in netstandard2.1

    static uint Round(uint acc, uint input)
    {
        acc += input * Prime2;
        acc = Rotl(acc, 13);
        return acc * Prime1;
    }

    static uint Finalize(uint h, ReadOnlySpan<byte> tail)
    {
        while (tail.Length >= 4)
        {
            h += BinaryPrimitives.ReadUInt32LittleEndian(tail) * Prime3;
            h = Rotl(h, 17) * Prime4;
            tail = tail.Slice(4);
        }

        foreach (var b in tail)
        {
            h += b * Prime5;
            h = Rotl(h, 11) * Prime1;
        }

        h ^= h >> 15;
        h *= Prime2;
        h ^= h >> 13;
        h *= Prime3;
        h ^= h >> 16;
        return h;
    }
}
