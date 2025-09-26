using NativeCompressions.Interop;
using System.Runtime.InteropServices;

namespace NativeCompressions;

// LZ4F_decompressOptions_t + dictionary ref

[StructLayout(LayoutKind.Auto)]
public readonly record struct LZ4DecompressionOptions
{
    public static readonly LZ4DecompressionOptions Default = new LZ4DecompressionOptions();

    readonly uint stableDst;
    readonly uint skipChecksums;

    // other reference
    readonly LZ4Dictionary? dictionary;

    /// <summary>
    /// pledges that last 64KB decompressed data is present right before @dstBuffer pointer.
    /// This optimization skips internal storage operations.
    /// Once set, this pledge must remain valid up to the end of current frame.
    /// </summary>
    public bool StableDst { get => stableDst == 1; init => stableDst = (value) ? 1u : 0; }

    /// <summary>
    /// disable checksum calculation and verification, even when one is present in frame, to save CPU time.
    /// Setting this option to 1 once disables all checksums for the rest of the frame.
    /// </summary>
    public bool SkipChecksums { get => skipChecksums == 1; init => skipChecksums = (value) ? 1u : 0; }

    public LZ4Dictionary? Dictionary
    {
        get
        {
            return dictionary;
        }
        init
        {
            dictionary = value;
        }
    }

    internal unsafe LZ4F_decompressOptions_t ToDecompressOptions()
    {
        var options = new LZ4F_decompressOptions_t
        {
            skipChecksums = skipChecksums,
            stableDst = stableDst
        };

        return options;
    }
}
