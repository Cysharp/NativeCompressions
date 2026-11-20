using NativeCompressions.Interop;
using System.Runtime.InteropServices;

namespace NativeCompressions;

// LZ4F_decompressOptions_t + dictionary ref
// stableDst of LZ4F_decompressOptions_t is left out. With it lz4frame keeps the address of the previous destination
// between calls, and the GC can move a managed buffer in the meantime, so the decoder would read whatever is there now.

[StructLayout(LayoutKind.Auto)]
public readonly record struct LZ4DecompressionOptions
{
    public static readonly LZ4DecompressionOptions Default = new LZ4DecompressionOptions();

    readonly uint skipChecksums;

    // other reference
    readonly LZ4Dictionary? dictionary;

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

    internal LZ4Dictionary.Lease AcquireDictionary() => dictionary == null ? default : dictionary.Acquire();

    internal unsafe LZ4F_decompressOptions_t ToDecompressOptions()
    {
        // stableDst stays 0, lz4frame then copies the history it needs into its own buffer before every call returns
        var options = new LZ4F_decompressOptions_t
        {
            skipChecksums = skipChecksums,
        };

        return options;
    }
}
