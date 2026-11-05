# Changelog

All notable changes to this project are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- `LZ4Stream.BaseStream`, `LZ4Encoder.Reset(in LZ4CompressionOptions)` and `LZ4Decoder.Reset(in LZ4DecompressionOptions)`, matching the Zstandard side.
- XML documentation is included in the Core packages.
- `CHANGELOG.md`.

### Changed
- `LZ4Encoder.Compress`, `Flush` and `Close` return an `OperationStatus` with `bytesConsumed` and `bytesWritten`, and `Compress` takes `isFinalBlock`, the same as `ZstandardEncoder` and `BrotliEncoder`. Any destination size works. A call that runs out of room returns `DestinationTooSmall`, and the next call continues. A failure, such as a frame length that differs from `ContentSize`, returns `InvalidData` instead of throwing `LZ4Exception`, and the encoder needs `Reset()` afterwards.
- `LZ4Encoder.GetMaxCompressedLength` counts the frame header the options produce instead of the largest possible one.
- `LZ4.Block.Decompress` reports a destination that is too small and a missing or wrong dictionary with their own messages.
- The bundled LZ4 is v1.11.0.
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

### Fixed
- Disposing an encoder or decoder from two threads at once could release its dictionary lease twice.
- A failed `ZstandardEncoder.Reset(in options)` restores the previous options, including a dictionary the caller disposed meanwhile. Previously it fell back to the default options.
- `LZ4.Decompress(source, destination)` threw "Destination buffer is too small" for a valid frame whose last blocks produce no output, such as an empty block before the end mark, when the destination had exactly the size of the content. Found by fuzzing.
- `ZstandardDictionary.Train` crashed natively when the first 75% of the samples, the part zstd trains on, held fewer than 8 bytes in total. zstd only checks the total of all samples. The binding rejects such input with `ZstandardException`. Found by fuzzing.
