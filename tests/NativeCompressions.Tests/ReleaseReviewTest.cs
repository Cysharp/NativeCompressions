using NativeCompressions.Interop;
using System.Buffers;
using System.Reflection;
using System.Text;
using CompressionMode = System.IO.Compression.CompressionMode;

namespace NativeCompressions.Tests;

// Covers the API decisions made before 1.0: naming, default values, validation, and the parity between LZ4 and Zstandard.
public class ReleaseReviewTest
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] Sample() => Utf8(string.Concat(Enumerable.Repeat("native compressions release review sample ", 500)));

    // ---- default(ZstandardCompressionOptions) is the default

    [Fact]
    public void Zstandard_UninitializedOptions_EqualDefault()
    {
        ZstandardCompressionOptions uninitialized = default;

        Assert.True(uninitialized.IsDefault);
        Assert.Equal(ZstandardCompressionOptions.Default, uninitialized);
        Assert.True(uninitialized.ContentSizeFlag);
        Assert.True(uninitialized.DictIdFlag);
        Assert.False(uninitialized.ChecksumFlag);

        var data = Sample();
        var compressed = Zstandard.Compress(data, uninitialized);
        Assert.True(Zstandard.TryGetFrameContentSize(compressed, out var size));
        Assert.Equal((ulong)data.Length, size);

        // the flags whose default is on can still be turned off from an uninitialized struct
        var flagsOff = uninitialized with { ContentSizeFlag = false, DictIdFlag = false };
        Assert.False(flagsOff.ContentSizeFlag);
        Assert.False(flagsOff.DictIdFlag);
        Assert.False(flagsOff.IsDefault);
        Assert.False(Zstandard.TryGetFrameContentSize(Zstandard.Compress(data, flagsOff), out _));
    }

    // ---- Try methods return false, they do not throw

    [Fact]
    public void Zstandard_TryGetFrameContentSize_ReturnsFalseForInvalidInput()
    {
        Assert.False(Zstandard.TryGetFrameContentSize(ReadOnlySpan<byte>.Empty, out var size));
        Assert.Equal(0UL, size);
        Assert.False(Zstandard.TryGetFrameContentSize(new byte[] { 1, 2, 3 }, out _));

        var garbage = new byte[64];
        new Random(1).NextBytes(garbage);
        Assert.False(Zstandard.TryGetFrameContentSize(garbage, out _));
        Assert.False(Zstandard.TryGetMaxDecompressedLength(garbage, out _));

        var noSize = Zstandard.Compress(Sample(), ZstandardCompressionOptions.Default with { ContentSizeFlag = false });
        Assert.False(Zstandard.TryGetFrameContentSize(noSize, out _));
    }

    // ---- compression level validation

    [Theory]
    [InlineData(999)]
    [InlineData(-999_999)]
    public void Zstandard_CompressionLevelOutOfRange_Throws(int level)
    {
        var data = Sample();
        var destination = new byte[Zstandard.GetMaxCompressedLength(data.Length)];

        Assert.Throws<ArgumentOutOfRangeException>(() => Zstandard.Compress(data, level));
        Assert.Throws<ArgumentOutOfRangeException>(() => Zstandard.Compress(data, destination, level));
        Assert.Throws<ArgumentOutOfRangeException>(() => Zstandard.TryCompress(data, destination, out _, level));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstandardEncoder(level));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstandardCompressionOptions(level));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZstandardCompressionOptions.Default with { CompressionLevel = level });
        Assert.Throws<ArgumentOutOfRangeException>(() => ZstandardDictionary.Create(Utf8("dictionary"), level));
    }

    [Fact]
    public void Zstandard_CompressionLevelBounds_AreAccepted()
    {
        var data = Sample();
        foreach (var level in new[] { Zstandard.MinCompressionLevel, 0, Zstandard.DefaultCompressionLevel, Zstandard.MaxCompressionLevel })
        {
            Assert.Equal(data, Zstandard.Decompress(Zstandard.Compress(data, level)));
            Assert.Equal(data, Zstandard.Decompress(Zstandard.Compress(data, new ZstandardCompressionOptions(level))));
        }
    }

    // ---- naming

    [Fact]
    public void LZ4_IdProperties_AndBoolFavorDecompressionSpeed()
    {
        using var dict = LZ4Dictionary.Create(Utf8("dictionary bytes for the id check"), 1234);
        var options = LZ4CompressionOptions.Default with
        {
            Dictionary = dict,
            BlockSizeId = BlockSizeId.Max256KB,
            CompressionLevel = 10,
            FavorDecompressionSpeed = true,
        };
        Assert.Equal(1234u, options.DictionaryId);
        Assert.True(options.FavorDecompressionSpeed);
        Assert.False(LZ4CompressionOptions.Default.FavorDecompressionSpeed);

        // one-shot compression shrinks the block size to the input, so the input has to be larger than 64KB
        var data = Utf8(string.Concat(Enumerable.Repeat("native compressions release review sample ", 2500)));
        var compressed = LZ4.Compress(data, options);
        Assert.True(LZ4.TryGetFrameInfo(compressed, out var info));
        Assert.Equal(1234u, info.DictionaryId);
        Assert.Equal(BlockSizeId.Max256KB, info.BlockSizeId);
        Assert.Equal(data, LZ4.Decompress(compressed, LZ4DecompressionOptions.Default with { Dictionary = dict }));
    }

    [Fact]
    public void VersionNumber_IsIntOnBothSides()
    {
        Assert.Equal(typeof(int), typeof(LZ4).GetField(nameof(LZ4.VersionNumber))!.FieldType);
        Assert.Equal(typeof(int), typeof(Zstandard).GetField(nameof(Zstandard.VersionNumber))!.FieldType);
        Assert.True(LZ4.VersionNumber > 0);
        Assert.True(Zstandard.VersionNumber > 0);
    }

    [Fact]
    public void OptionsParameters_AreNamedOptions()
    {
        var types = new[]
        {
            typeof(LZ4), typeof(Zstandard),
            typeof(LZ4Encoder), typeof(LZ4Decoder), typeof(ZstandardEncoder), typeof(ZstandardDecoder),
            typeof(LZ4Stream), typeof(ZstandardStream),
        };

        foreach (var type in types)
        {
            var members = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Cast<MethodBase>()
                .Concat(type.GetConstructors());
            foreach (var member in members)
            {
                foreach (var parameter in member.GetParameters())
                {
                    var parameterType = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
                    parameterType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
                    if (parameterType.Name.EndsWith("CompressionOptions", StringComparison.Ordinal))
                    {
                        Assert.True(parameter.Name == "options", $"{type.Name}.{member.Name} names its {parameterType.Name} parameter '{parameter.Name}'");
                    }
                }
            }
        }
    }

    // ---- LZ4 has what Zstandard has

    [Fact]
    public void LZ4Stream_BaseStream()
    {
        var inner = new MemoryStream();
        using (var stream = new LZ4Stream(inner, CompressionMode.Compress, leaveOpen: true))
        {
            Assert.Same(inner, stream.BaseStream);
        }

        var disposed = new LZ4Stream(new MemoryStream(), CompressionMode.Decompress);
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => disposed.BaseStream);
    }

    [Fact]
    public void LZ4Encoder_ResetWithOptions()
    {
        var data = Sample();
        using var encoder = new LZ4Encoder(LZ4CompressionOptions.Default);
        var buffer = new byte[encoder.GetMaxCompressedLength(data.Length)];

        // a frame in progress is abandoned
        encoder.Compress(data.AsSpan(0, 100), buffer);

        var dict = LZ4Dictionary.Create(Utf8("lz4 reset dictionary"), 77);
        encoder.Reset(LZ4CompressionOptions.Default with
        {
            Dictionary = dict,
            ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled,
            ContentSize = (ulong)data.Length,
        });

        var written = encoder.Compress(data, buffer);
        written += encoder.Close(buffer.AsSpan(written));
        var compressed = buffer.AsSpan(0, written).ToArray();

        Assert.True(LZ4.TryGetFrameInfo(compressed, out var info));
        Assert.Equal(77u, info.DictionaryId);
        Assert.Equal(ContentChecksum.ContentChecksumEnabled, info.ContentChecksumFlag);
        Assert.Equal((ulong)data.Length, info.ContentSize);
        Assert.Equal(data, LZ4.Decompress(compressed, LZ4DecompressionOptions.Default with { Dictionary = dict }));

        // back to the defaults, the encoder releases the dictionary and keeps working after it is disposed
        encoder.Reset(LZ4CompressionOptions.Default);
        dict.Dispose();

        written = encoder.Compress(data, buffer);
        written += encoder.Close(buffer.AsSpan(written));
        compressed = buffer.AsSpan(0, written).ToArray();
        Assert.True(LZ4.TryGetFrameInfo(compressed, out info));
        Assert.Equal(0u, info.DictionaryId);
        Assert.Equal(data, LZ4.Decompress(compressed));
    }

    [Fact]
    public void LZ4Decoder_ResetWithOptions()
    {
        var data = Sample();
        var dict = LZ4Dictionary.Create(Utf8(string.Concat(Enumerable.Repeat("native compressions release review sample ", 8))), 5);
        var withDict = LZ4.Compress(data, LZ4CompressionOptions.Default with { Dictionary = dict });
        var plain = LZ4.Compress(data);

        using var decoder = new LZ4Decoder();
        var dest = new byte[data.Length];

        // part of a plain frame is abandoned, then the decoder switches to the dictionary
        decoder.Decompress(plain.AsSpan(0, 20), dest, out _, out _);
        decoder.Reset(LZ4DecompressionOptions.Default with { Dictionary = dict });

        Assert.Equal(OperationStatus.Done, decoder.Decompress(withDict, dest, out var consumed, out var written));
        Assert.Equal(withDict.Length, consumed);
        Assert.Equal(data.Length, written);
        Assert.Equal(data, dest);

        // and back, the decoder releases the dictionary
        decoder.Reset(LZ4DecompressionOptions.Default);
        dict.Dispose();
        Array.Clear(dest);

        Assert.Equal(OperationStatus.Done, decoder.Decompress(plain, dest, out _, out written));
        Assert.Equal(data.Length, written);
        Assert.Equal(data, dest);
    }

    // ---- Flush does nothing while decompressing

    [Fact]
    public async Task Stream_Flush_DoesNothingWhileDecompressing()
    {
        var data = Sample();

        using (var stream = new LZ4Stream(new MemoryStream(LZ4.Compress(data)), CompressionMode.Decompress))
        {
            stream.Flush();
            await stream.FlushAsync();
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(data, ms.ToArray());
        }

        using (var stream = new ZstandardStream(new MemoryStream(Zstandard.Compress(data)), CompressionMode.Decompress))
        {
            stream.Flush();
            await stream.FlushAsync();
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(data, ms.ToArray());
        }
    }

    // ---- header and footer lengths

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void LZ4Encoder_ActualHeaderAndFooterLength_MatchTheFrame(bool contentSize, bool dictionary, bool checksum)
    {
        var data = Sample();
        using var dict = dictionary ? LZ4Dictionary.Create(Utf8("header length dictionary"), 9) : null;
        using var encoder = new LZ4Encoder(LZ4CompressionOptions.Default with
        {
            ContentSize = contentSize ? (ulong)data.Length : 0,
            Dictionary = dict,
            ContentChecksumFlag = checksum ? ContentChecksum.ContentChecksumEnabled : ContentChecksum.NoContentChecksum,
        });

        var buffer = new byte[encoder.GetMaxCompressedLength(data.Length)];

        var headerLength = encoder.Compress([], buffer); // only the header
        Assert.Equal(encoder.GetActualFrameHeaderLength(), headerLength);
        Assert.Equal(7 + (contentSize ? 8 : 0) + (dictionary ? 4 : 0), headerLength);
        using (var decoder = new LZ4Decoder())
        {
            Assert.Equal(headerLength, decoder.GetHeaderSize(buffer.AsSpan(0, headerLength)));
        }

        var written = headerLength;
        written += encoder.Compress(data, buffer.AsSpan(written));
        written += encoder.Flush(buffer.AsSpan(written)); // only the footer is left

        var footerLength = encoder.Close(buffer.AsSpan(written));
        Assert.Equal(encoder.GetActualFrameFooterLength(), footerLength);
        Assert.Equal(4 + (checksum ? 4 : 0), footerLength);
        written += footerLength;

        Assert.True(encoder.GetMaxFlushBufferLength(includingFooter: true) >= headerLength + footerLength);
        Assert.Equal(data, LZ4.Decompress(buffer.AsSpan(0, written).ToArray(), LZ4DecompressionOptions.Default with { Dictionary = dict }));
    }

    // ---- block format with a dictionary

    [Fact]
    public unsafe void LZ4Block_DecompressWithDictionary()
    {
        // LZ4.Block.Compress has no dictionary overload, so the block is produced with the streaming C API.
        var dictBytes = Utf8(string.Concat(Enumerable.Repeat("block dictionary ", 64)));
        var data = Utf8(string.Concat(Enumerable.Repeat("block dictionary payload ", 64)));
        using var dict = LZ4Dictionary.Create(dictBytes, 1);

        var compressed = new byte[LZ4.Block.GetMaxCompressedLength(data.Length)];
        int compressedLength;
        var state = LZ4NativeMethods.LZ4_createStream();
        try
        {
            fixed (byte* d = dictBytes)
            fixed (byte* src = data)
            fixed (byte* dst = compressed)
            {
                Assert.Equal(dictBytes.Length, LZ4NativeMethods.LZ4_loadDict(state, d, dictBytes.Length));
                compressedLength = LZ4NativeMethods.LZ4_compress_fast_continue(state, src, dst, data.Length, compressed.Length, 1);
            }
        }
        finally
        {
            LZ4NativeMethods.LZ4_freeStream(state);
        }
        Assert.True(compressedLength > 0);
        var block = compressed.AsSpan(0, compressedLength).ToArray();

        var dest = new byte[data.Length];
        Assert.Equal(data.Length, LZ4.Block.Decompress(block, dest, dict));
        Assert.Equal(data, dest);

        // the block references the dictionary, so it cannot be decoded without it
        Assert.Throws<LZ4Exception>(() => LZ4.Block.Decompress(block, new byte[data.Length]));

        // a destination that is too small
        Assert.Throws<LZ4Exception>(() => LZ4.Block.Decompress(block, new byte[data.Length - 1], dict));
    }

    // ---- found by fuzzing: a frame may end with blocks that produce no output after the destination is full

    [Fact]
    public void LZ4_DecompressToSpan_EmptyBlockAfterContent()
    {
        var data = Sample();
        var frame = LZ4.Compress(data, LZ4CompressionOptions.Default with { BlockMode = BlockMode.BlockIndependent });

        // insert a block of one byte, the token 0, which decodes to nothing, right before the 4 byte end mark
        var emptyBlock = new byte[] { 1, 0, 0, 0, 0 };
        var withEmptyBlock = frame.AsSpan(0, frame.Length - 4).ToArray().Concat(emptyBlock).Concat(frame.AsSpan(frame.Length - 4).ToArray()).ToArray();
        Assert.Equal(data, LZ4.Decompress(withEmptyBlock)); // the array overload already handled it

        var exact = new byte[data.Length];
        Assert.Equal(data.Length, LZ4.Decompress(withEmptyBlock, exact));
        Assert.Equal(data, exact);

        var tooSmall = Assert.Throws<LZ4Exception>(() => LZ4.Decompress(withEmptyBlock, new byte[data.Length - 1]));
        Assert.Contains("too small", tooSmall.Message);

        // a full destination with input that ends before the frame does is still reported as truncated input
        var truncated = Assert.Throws<LZ4Exception>(() => LZ4.Decompress(withEmptyBlock.AsSpan(0, withEmptyBlock.Length - 3).ToArray(), new byte[data.Length]));
        Assert.Contains("ends inside a frame", truncated.Message);
    }

    // ---- found by fuzzing: zstd reads out of bounds when the training part of the samples is shorter than 8 bytes

    [Fact]
    public void ZstandardDictionary_Train_RejectsTooShortTrainingSamples()
    {
        // 58 empty samples and one of 15 bytes, the total passes the check of zstd but the first 75% hold nothing
        var lengths = Enumerable.Repeat(0, 58).Append(15).ToArray();
        var samples = Utf8("fifteen bytes!!");
        Assert.Throws<ZstandardException>(() => ZstandardDictionary.Train(samples, lengths, 256));

        // six one byte samples before a long one, the training part is 5 bytes
        lengths = [1, 1, 1, 1, 1, 1, 100];
        samples = new byte[106];
        Assert.Throws<ZstandardException>(() => ZstandardDictionary.Train(samples, lengths, 256));

        // enough training bytes, zstd decides
        var text = Utf8(string.Concat(Enumerable.Repeat("zstd dictionary training sample text ", 400)));
        var trained = ZstandardDictionary.Train(text, Enumerable.Repeat(text.Length / 40, 40).ToArray(), 4096);
        Assert.NotEmpty(trained);
    }

    // ---- a failed Reset keeps the previous options, also when their dictionary was disposed meanwhile

    [Fact]
    public void ZstandardEncoder_FailedReset_KeepsPreviousOptionsAndDictionary()
    {
        var data = Sample();
        var dictBytes = Utf8(string.Concat(Enumerable.Repeat("native compressions release review sample ", 8)));
        var dict = ZstandardDictionary.Create(dictBytes);
        var previous = ZstandardCompressionOptions.Default with { Dictionary = dict, ChecksumFlag = true, CompressionLevel = 5 };
        using var encoder = new ZstandardEncoder(previous);

        // the encoder holds a lease, so the caller may dispose the dictionary
        dict.Dispose();
        Assert.True(dict.IsDisposed);

        // new options that still name the disposed dictionary fail before anything is applied
        Assert.Throws<ObjectDisposedException>(() => encoder.Reset(previous with { CompressionLevel = 1 }));

        // WindowLog beyond the bounds is rejected by zstd after other parameters were already applied
        Assert.Throws<ZstandardException>(() => encoder.Reset(ZstandardCompressionOptions.Default with { WindowLog = 1000 }));

        var buffer = new byte[Zstandard.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, encoder.Compress(data, buffer, out _, out var written, isFinalBlock: true));
        var compressed = buffer.AsSpan(0, written).ToArray();

        // Frame_Header_Descriptor bit 2 is the content checksum flag
        Assert.NotEqual(0, compressed[4] & 0x04);

        // the frame still uses the dictionary, so it needs one made of the same bytes
        using var same = ZstandardDictionary.Create(dictBytes);
        Assert.Equal(data, Zstandard.Decompress(compressed, ZstandardDecompressionOptions.Default with { Dictionary = same }));
        Assert.Throws<ZstandardException>(() => Zstandard.Decompress(compressed));
    }
}
