NativeCompressions
===
<!-- [![CI](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-debug.yaml/badge.svg)](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-debug.yaml)
[![NuGet](https://img.shields.io/nuget/v/NativeCompressions)](https://www.nuget.org/packages/NativeCompressions) -->

NativeCompressions provides native library bindings, streaming processing, and multi-threading support for [LZ4](https://github.com/lz4/lz4) with its excellent decompression speed, and [Zstandard](https://github.com/facebook/zstd) with its superior balance of compression ratio and performance.

![](https://github.com/user-attachments/assets/5ab559ef-86ca-42ba-add7-b6904a335409)

![](https://github.com/user-attachments/assets/3eed676a-e3b5-411b-95c7-6be6896f991a)

> Encode [silesia.tar](https://en.wikipedia.org/wiki/Silesia_corpus) corpus(200MB) with default LZ4 options / Zstandard options.

Compression is crucial for any application, but .NET has had limited options. NativeCompressions builds state-of-the-art algorithms (LZ4, Zstandard) with allocation-free, stream-less streaming APIs. Furthermore, by leveraging modern C# APIs (`Span<T>`, `RandomAccess`, `PipeReader/Writer`) to provide high-level multi-threading APIs, we achieve high-performance compression in any environment.

We chose native bindings over Pure C# implementation because compression library performance depends not only on algorithms but also on implementation. LZ4 and Zstandard are actively developed with performance improvements in every release. It's impossible to keep synchronizing advanced memory operations and CPU architecture optimizations with .NET ports. To continuously provide the best and latest performance, native bindings are necessary. Note that .NET's standard `System.IO.Compression.BrotliEncoder/Decoder` links [brotli](https://github.com/dotnet/runtime/tree/main/src/native/external/brotli) to [libSystem.IO.Compression.Native](https://github.com/dotnet/runtime/tree/main/src/native/libs/System.IO.Compression.Native), also DeflateStream/GZipStream uses native zlib (from .NET 9, it's [zlib-ng](https://github.com/zlib-ng/zlib-ng)), meaning we follow the same adoption criteria as .NET official.

LZ4 and Zstandard are created by the same author [Cyan4973](https://github.com/Cyan4973), showing high performance against competitors in their respective domains (LZ4 vs Snappy / Zstandard vs Brotli), and are widely used as industry standards.

> [!NOTE]
> This library is in preview. We do not recommend using it in production environments. The API may change. We are collecting feedback during this preview period.

Getting Started
---
Install the package from [NuGet/NativeCompressions](https://www.nuget.org/packages/NativeCompressions):

```bash
dotnet add package NativeCompressions
```

```csharp
// for LZ4
using NativeCompressions.LZ4;

// Simple compression
byte[] compressed = LZ4.Compress(sourceData);
byte[] decompressed = LZ4.Decompress(compressed);
```

```csharp
// for Zstandard
using NativeCompressions.Zstandard;

// Simple compression
byte[] compressed = Zstandard.Compress(sourceData);
byte[] decompressed = Zstandard.Decompress(compressed);
```

Install for Unity, see [Unity](#unity) section.

LZ4
---
LZ4 has both block format and frame format. We adopt frame format for all APIs from the perspective of compatibility, security, and performance flexibility. External dictionary loading is also supported.

### Simple Compression

Simple API to convert from `ReadOnlySpan<T>` to `byte[]`, or write/read to/from `Span<T>`. These encode/decode in frame format, not block format. Also automatically sets ContentSize in the frame header.

```csharp
using NativeCompressions.LZ4;

// ReadOnlySpan<byte> convert to byte[]
byte[] compressed = LZ4.Compress(source);
byte[] decompressed = LZ4.Decompress(compressed);

// ReadOnlySpan<byte> write to Span<byte>
var maxSize = LZ4.GetMaxCompressedLength(source.Length);
var destinationBuffer = new byte[maxSize];
var written = LZ4.Compress(source, destinationBuffer);
var destination = destinationBuffer[0..written];
```

These APIs can be customized by passing `LZ4FrameOptions` or `LZ4CompressionDictionary`. When decompressing to `byte[]`, setting the `bool trustedData` argument to `true` will trust the `ContentSize` in the LZ4 frame header if present, pre-allocating the buffer for improved performance. When `false`, it processes in blocks to an internal buffer then concatenates, which is more resistant to attacks sending malicious data. Default is `false`.

### Low-level Streaming Compression

APIs similar to [BrotliEncoder](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.brotliencoder)/[BrotliDecoder](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.brotliencoder) in System.IO.Compression to encode and decode data in a streamless, non-allocating, and performant manner using the LZ4 frame format specification.

```csharp
using NativeCompressions.LZ4;

// for example, use for IBufferWriter<byte>
IBufferWriter<byte> bufferWriter;

using var encoder = new LZ4Encoder();

// Compress chunks(invoke Compress multiple times)
foreach (var chunk in dataChunks) // dataChunks = byte[][]
{
    // get max size per streaming compress
    var size = encoder.GetMaxCompressedLength(chunk.Length);

    var buffer = bufferWriter.GetSpan(size);

    // written size can be zero, meaning input data was just buffered.
    var written = encoder.Compress(chunk, buffer);

    bufferWriter.Advance(written);
}

// Finalize frame(need to get size of footer with buffered-data)
var fotterWithBufferdDataSize = encoder.GetMaxFlushBufferLength(includingFooter: true);
var finalBytes = bufferWriter.GetSpan(fotterWithBufferdDataSize);

// need to call `Close` to write LZ4 frame footer
var finalWritten = encoder.Close(finalBytes);

bufferWriter.Advance(finalWritten);
```

Method name is `Compress` not `TryCompress`, and returns size not `OperationStatus` because LZ4's native API differs from Brotli. The source is fully consumed, requiring destination to be at least MaxCompressedLength relative to source. On failure, the internal context state is corrupted, so it cannot be a Try... API and throws `LZ4Exception` on failure.

For decompression, use `LZ4Decoder`.

```csharp
// while(status != OperationStatus.Done && source.Length > 0)
OperationStatus status = decoder.Decompress(source, destination, out int bytesConsumed, out int bytesWritten);
source = source.Slice(bytesConsumed);
destination = destination.Slice(bytesWritten);
```

In `Decompress`, both source and destination can receive incomplete data. When `OperationStatus.Done` is returned, all data is restored. Otherwise, `NeedMoreData` or `DestinationTooSmall` is returned.

### High-level Streaming Compression

Using `CompressAsync` or `DecompressAsync`, you can stream encode/decode from `ReadOnlyMemory<byte>`, `ReadOnlySequence<byte>`, `SafeFileHandle`, `Stream`, `PipeReader` to `PipeWriter`. While internally using `LZ4Encoder/LZ4Decoder`, a single method call optimally handles complex operations.

The destination `PipeWriter` can be passed directly or wrapped around a Stream to change where to write.

```csharp
// to Memory
using var ms = new MemoryStream();
await LZ4.CompressAsync(source, PipeWriter.Create(ms));

// to File
using var fs = new FileStream("foo.lz4", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, bufferSize: 1, useAsync: true);
await LZ4.CompressAsync(source, PipeWriter.Create(fs));

// to Network
using var fs = new NetworkStream(socket);
await LZ4.CompressAsync(source, PipeWriter.Create(fs));
```

When source is `ReadOnlyMemory<byte>`, `ReadOnlySequence<byte>`, or `SafeFileHandle`, you can specify `int? maxDegreeOfParallelism`. If null or 2 or greater, parallel processing occurs when the target source is 1MB or larger. null (default) uses `Environment.ProcessorCount`. Specifying 1 always results in sequential processing.

```csharp
// Parallel Compression from File to File
using SafeFileHandle sourceHandle = File.OpenHandle("foo.bin");
using var dest = new FileStream("foo.lz4", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, bufferSize: 1, useAsync: true);
await LZ4.CompressAsync(sourceHandle, PipeWriter.Create(dest), maxDegreeOfParallelism: null);
```

When source is a file, passing `SafeFileHandle` enables parallel processing and can expect higher performance than `FileStream`. `Stream` or `PipeReader` doesn't perform parallel processing because the maximum source length is unknown, preventing estimation of appropriate block size for division.

> For ASP.NET servers, we recommend always specifying maxDegreeOfParallelism as 1. Since the server itself processes requests in parallel, increasing CPU load may reduce overall throughput. Parallel processing will be highly effective in client applications or CLI batch processing.

Similarly for Decompress, source can be `ReadOnlyMemory<byte>`, `ReadOnlySequence<byte>`, `SafeFileHandle`, `Stream`, `PipeReader`, and destination can be `PipeWriter`.

```csharp
using var ms = new MemoryStream();
using SafeFileHandle sourceHandle = File.OpenHandle("foo.lz4");
await LZ4.DecompressAsync(source, PipeWriter.Create(ms));

var decompressed = ms.ToArray();
```

Decompress parallel processing occurs when the LZ4 frame is compressed with `BlockIndependent` and `maxDegreeOfParallelism` is null or 2 or greater. In NativeCompressions, normal LZ4 compression processes with `BlockLinked`, but only compresses as `BlockIndependent` when parallel processing in `CompressAsync`.

Similar to Compress, explicitly specifying maxDegreeOfParallelism as 1 is recommended for ASP.NET servers.

### Stream
Compatible with System.IO.Stream for easy integration:

```csharp
// Compression stream
using var output = new MemoryStream();
using (var lz4Stream = new LZ4Stream(output, CompressionMode.Compress))
{
    await inputStream.CopyToAsync(lz4Stream);
} // Auto-close writes frame footer

// Decompression stream
using var input = new MemoryStream(compressedData);
using var lz4Stream = new LZ4Stream(input, CompressionMode.Decompress);
byte[] buffer = new byte[4096];
int read = await lz4Stream.ReadAsync(buffer);
```

### Options
You can change `with` operator.

```csharp
var options = LZ4FrameOptions.Default with
{
    CompressionLevel = 3,
    ContentSize = LZ4FrameOptions.Default with
    {
        ContentSize = source.Length
    }
};
```

```csharp
// full-options
var options = new LZ4FrameOptions
{
    CompressionLevel = 9,           // 0-12
    AutoFlush = true,               // Flush after each compress call
    FavorDecompressionSpeed = 1,    // Optimize for decompression
    FrameInfo = new LZ4FrameInfo
    {
        BlockSizeID = BlockSizeId.Max4MB,  // Max64KB, Max256KB, Max1MB, Max4MB
        BlockMode = BlockMode.BlockIndependent, 
        ContentChecksumFlag = ContentChecksum.ContentChecksumEnabled,
        BlockChecksumFlag = BlockChecksum.BlockChecksumEnabled,
        ContentSize = (ulong)sourceData.Length  // Pre-declare size
    }
};
```

### Dictionary Compression
Improve compression ratio for similar data:

```csharp
// Create dictionary from sample data
var dictionary = new LZ4CompressionDictionary(sampleData, dictionaryId: 12345);

// Use dictionary for compression
byte[] compressed = LZ4.Compress(source, LZ4FrameOptions.Default, dictionary);

// Decompression with same dictionary
byte[] decompressed = LZ4.Decompress(compressed, dictionary);

// Dictionary can be reused across multiple operations
using var encoder = new LZ4Encoder(options, dictionary);
```

### Block Compression
For raw LZ4 block compression without frame format:

```csharp
// Get max compressed size
var maxSize = LZ4.Block.GetMaxCompressedLength(source.Length);

// Compress block
var destination = new byte[maxSize];
var compressedSize = LZ4.Block.Compress(source, destination);
var compressed = destination.AsSpan(0, compressedSize).ToArray();

// Decompress block
destination = new byte[source.Length];
var decompressedSize = LZ4.Block.Decompress(compressed, destination);
var decompressed = destination.AsSpan(0, decompressedSize).ToArray();
```

### Raw API
TODO

Zstandard
---
It is generally similar to the LZ4 API. The `Zstandard` class has static methods, and there are `ZstandardEncoder` and `ZstandardDecoder` as Streamless-streaming APIs. Currently, the PipeReader/PipeWriter API is not implemented, but it will eventually be provided.

Detailed documentation will also be prepared later.

Telemetry
---
TODO

Unity
---
Install `NativeCompressions` from NuGet using [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity). Open Window from NuGet -> Manage NuGet Packages, Search "NativeCompressions" and Press Install.

The `NativeCompressions` package includes all runtimes. If you want to install only specific runtimes, please install the `Core` package and `Runtime.***` packages separately.

NuGetForUnity basically handles native runtimes correctly, but there are some that are not currently supported. For example, win-arm64, linux-arm64, android-arm, android-x64, and ios-x64 cannot be imported. As a workaround, you can replace `ProjectSettings/Packages/com.github-glitchenzo.nugetforunity/NativeRuntimeSettings.json` with this [NativeRuntimeSettings.json](https://github.com/Cysharp/NativeCompressions/blob/6123b5a/sandbox/UnityApp/ProjectSettings/Packages/com.github-glitchenzo.nugetforunity/NativeRuntimeSettings.json) to enable import support. I have submitted a PR to NuGetForUnity to support this by default, but until that is released, please use the above workaround.

The current preview does not support IL2CPP builds for iOS. We plan to support this in the official release. It works without issues on all other platforms.

License
---
This library is licensed under the MIT License.

This library includes precompiled binaries of LZ4 and Zstandard. See LICENSE file for full license texts.

### Third-party Notices
* LZ4 - [Licensed under BSD 2-Clause license](https://github.com/lz4/lz4/blob/dev/LICENSE)
* Zstandard - [Licensed under BSD License](https://github.com/facebook/zstd/blob/dev/LICENSE)
