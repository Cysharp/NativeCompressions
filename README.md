NativeCompressions
===
[![CI](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-debug.yaml/badge.svg)](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-debug.yaml)
[![NuGet](https://img.shields.io/nuget/v/NativeCompressions)](https://www.nuget.org/packages/NativeCompressions)





NativeCompressions is the native ZStandard and LZ4 compression library and managed binding for .NET and Unity. Compression is very important, but has not been given much importance in .NET. We chose native binding, because ZStandard and LZ4 are constantly evolving and introducing new optimizations, and pure C# port doesn't get the benefit of that. Bindings were created with performance in mind to conform to modern .NET APIs(Span, IBufferWriter, System.IO.Pipelines, and .NET 6's new Scatter/Gather I/O API) and avoid allocation and marshalling overhead.


[NuGet/NativeCompressions](https://www.nuget.org/packages/NativeCompressions)  

```bash
dotnet add package NativeCompressions
```




Simple API

```csharp
var comp = ZStandard.Compress(input);
var bin = ZStandard.Decompress(comp);
```