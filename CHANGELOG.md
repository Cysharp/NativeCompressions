# Changelog

All notable changes to this project are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- `LZ4Stream.BaseStream`, `LZ4Encoder.Reset(in LZ4CompressionOptions)` and `LZ4Decoder.Reset(in LZ4DecompressionOptions)`, matching the Zstandard side.
- XML documentation is included in the Core packages.
- `CHANGELOG.md`.

### Changed
- `LZ4Encoder.Compress`, `Flush` and `Close` return an `OperationStatus` with `bytesConsumed` and `bytesWritten`, and `Compress` takes `isFinalBlock`, the same as `ZstandardEncoder` and `BrotliEncoder`. A call that runs out of room returns `DestinationTooSmall`, and the next call continues. Every call makes progress with a destination of at least `LZ4.MaxFrameHeaderLength` bytes. A failure, such as a frame length that differs from `ContentSize`, returns `InvalidData` with nothing counted, instead of throwing `LZ4Exception`, and the encoder needs `Reset()` afterwards. An empty source before the first input writes nothing, the header comes with the first input.
- The `LZ4Encoder` constructor throws `LZ4Exception` for options lz4 cannot begin a frame with, the same as `Reset(in LZ4CompressionOptions)`. Previously they failed at the first `Compress`.
- `LZ4Encoder.GetMaxCompressedLength` counts the frame header the options produce instead of the largest possible one. With `includingFooter` (the default) it also covers ending the frame in the same call with `isFinalBlock`, including the block the encoder still holds from earlier calls. `includingFooter: false` gives the smaller size for a call that does not end the frame.
- `LZ4Stream` passes a write larger than 1MB to the encoder in pieces, so its buffer stays within 2MB with 64KB and 256KB blocks, and within 4MB and 8MB with 1MB and 4MB blocks, which need room for a whole block. Previously it rented a buffer as large as the largest write.
- `LZ4.Block.Decompress` reports a destination that is too small and a missing or wrong dictionary with their own messages.
- The bundled LZ4 is v1.11.1.
- `LZ4CompressionOptions.BlockSizeID`, `LZ4CompressionOptions.DictionaryID`, `LZ4FrameInfo.BlockSizeID`, `LZ4FrameInfo.DictionaryID` and `ZstandardCompressionOptions.DictIDFlag` are renamed to `BlockSizeId`, `DictionaryId` and `DictIdFlag`.
- `LZ4CompressionOptions.FavorDecompressionSpeed` is a `bool`.
- `Zstandard.VersionNumber` is an `int`, like `LZ4.VersionNumber`.
- The options parameter of every Zstandard API is named `options`.
- `default(ZstandardCompressionOptions)` equals `ZstandardCompressionOptions.Default`. Previously the content size and the dictionary id were left out of the frame header when an uninitialized struct was passed.
- `Zstandard.TryGetFrameContentSize` returns false for input that is not a Zstandard frame, instead of throwing.
- A Zstandard compression level outside `Zstandard.MinCompressionLevel` to `Zstandard.MaxCompressionLevel` throws `ArgumentOutOfRangeException`, the same as `System.IO.Compression`. Previously it was clamped.
- `CompressAsync` and `DecompressAsync` throw `IOException` when the reader of the destination `PipeWriter` has completed. Previously it was `OperationCanceledException`, which a caller ignoring its own cancellation could swallow.
- `LZ4Stream.Flush` and `ZstandardStream.Flush` do nothing in Decompress mode, like the streams of `System.IO.Compression`. Previously they threw `InvalidOperationException`.
- A `Read` or `ReadAsync` with an empty buffer on `LZ4Stream` and `ZstandardStream` waits until the inner stream delivers input before returning 0, like the streams of `System.IO.Compression`, so a caller can delay renting a buffer until data is available. Previously it returned 0 at once, also in Compress mode, where it now throws `InvalidOperationException` like any other read.
- The macOS native libraries are built with an explicit minimum of macOS 15.0.
- The netstandard and net8.0 builds depend on the 10.0.x versions of `System.IO.Pipelines` and `Microsoft.Bcl.AsyncInterfaces`.
- `LZ4Decoder` and `ZstandardDecoder` return `OperationStatus.InvalidData` with nothing consumed and nothing written, and keep returning it until `Reset()`. `ZstandardEncoder` keeps returning `InvalidData` until `Reset()` as well, the same as `LZ4Encoder`. Previously a call after a failure reached the native context again.
- While `ZstandardEncoder` is ending a frame (a call with `isFinalBlock` returned `DestinationTooSmall` after taking all of the source), only `Close` or an empty source with `isFinalBlock` can follow, the same as `LZ4Encoder`. `Flush` or more input returns `InvalidData`.
- `Zstandard.CompressAsync` records the content size for a `ReadOnlySequence<byte>`, a seekable `Stream` and a file path, the same as `LZ4.CompressAsync`. Previously only a `ReadOnlyMemory<byte>` source did. The overloads that take a `ZstandardEncoder` leave it to the caller and `ZstandardEncoder.SetSourceLength`.
- `LZ4CompressionOptions` with a `BlockMode`, `ContentChecksumFlag` or `BlockChecksumFlag` that is not a defined value throws `ArgumentOutOfRangeException`. Previously lz4 took the number as it was and wrote a frame that does not decode.
- `LZ4.Block.GetMaxCompressedLength` throws `ArgumentOutOfRangeException` for a negative size and for one above 2,113,929,216 bytes, the largest input of a block. Previously it returned 0.
- `Zstandard.GetMaxCompressedLength` throws `ArgumentOutOfRangeException` for a negative size, previously `OverflowException`, and for a size zstd cannot compress in one frame, previously the error code as a length.
- `LZ4Decoder.GetFrameInfo` called again for the same frame returns the same information and consumes nothing.
- The Zstandard native libraries are built with `ZSTD_LEGACY_SUPPORT=0`. Frames of the formats before zstd v0.8 are invalid data now. Their decoders did not honor `WindowLogMax`.

### Fixed
- Calling `LZ4Decoder.Decompress` again after `OperationStatus.InvalidData`, or reading from an `LZ4Stream` again after it threw `LZ4Exception`, read outside the source and then copied the input over a native buffer of 64KB. lz4 cannot resume a context after an error.
- `ZstandardDecoder` reported the whole destination as written when a frame of a format before zstd v0.8 failed, and `Zstandard.DecompressAsync` passed that memory on to the destination before it threw.
- `ZstandardStream` threw `ZstandardException` for a valid frame, and for every read after it, once 15 reads in a row inside a frame got no data because the inner stream threw or the read was cancelled.
- `LZ4.CompressAsync` and `LZ4.DecompressAsync` threw `ArgumentOutOfRangeException` for an empty `ReadOnlyMemory<byte>` or `ReadOnlySequence<byte>` that does not start at index 0, such as `array.AsMemory(array.Length)`.
- `LZ4.Decompress(source, trustedData: true)` threw for a valid frame with a recorded content size whose last blocks produce no output, such as an empty block before the end mark.
- A second `LZ4Decoder.GetFrameInfo` before `Decompress` made a decoder with a dictionary decode the frame without it, which ended in `InvalidData`.
- A `Flush` that completed a frame `ZstandardEncoder` was ending left the encoder believing the frame was still open. The next frame was then not checked against the length given to `SetSourceLength`.
- A failed `ZstandardDecoder.Reset(in options)` keeps the previous options, like the encoder. Previously the decoder went on with default options, without its dictionary and its `WindowLogMax`.
- Decompressing more than 2GB into a `byte[]` ended in `OverflowException` after renting about 4GB. It is `OutOfMemoryException` now, or `LZ4Exception` for `trustedData`, as soon as the result cannot fit in an array.
- `ZstandardStream` passed a write of no bytes to the inner stream for every write the encoder only buffered.
- Disposing an encoder or decoder from two threads at once could release its dictionary lease twice.
- A failed `ZstandardEncoder.Reset(in options)` restores the previous options, including a dictionary the caller disposed meanwhile. Previously it fell back to the default options.
- `LZ4.Decompress(source, destination)` threw "Destination buffer is too small" for a valid frame whose last blocks produce no output, such as an empty block before the end mark, when the destination had exactly the size of the content. Found by fuzzing.
- `ZstandardDictionary.Train` crashed natively when the first 75% of the samples, the part zstd trains on, held fewer than 8 bytes in total. zstd only checks the total of all samples. The binding rejects such input with `ZstandardException`. Found by fuzzing.
