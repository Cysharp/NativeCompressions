# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

### Build
```bash
# Build entire solution in Debug mode
dotnet build -c Debug

# Build in Release mode
dotnet build -c Release

# Build specific project
dotnet build src/NativeCompressions.LZ4.Core/NativeCompressions.LZ4.Core.csproj
```

### Test
```bash
# Run all tests
dotnet test -c Release --no-build

# Run specific test project
dotnet test tests/NativeCompressions.Tests/NativeCompressions.Tests.csproj

# Run tests with filtering
dotnet test --filter "FullyQualifiedName~LZ4"
```

## Architecture

### Project Structure
The solution uses a multi-project structure with clear separation of concerns:

- **Core Libraries**: Located in `src/NativeCompressions.*.Core/`, these contain the main compression algorithm implementations
  - `NativeCompressions.LZ4.Core`: LZ4 compression implementation
  - `NativeCompressions.ZStandard.Core`: ZStandard compression implementation

- **Runtime Packages**: Platform-specific native binaries in `src/NativeCompressions.*.Runtime/`
  - Separate projects for each platform (win-x64, linux-x64, osx-arm64, etc.)
  - Native libraries are linked through P/Invoke in `Raw/NativeMethods.cs`

- **Meta Packages**: Aggregator packages in `src/` that bundle Core + Runtime dependencies
  - `NativeCompressions.LZ4.csproj`: Bundles LZ4 Core with all runtime packages
  - `NativeCompressions.csproj`: Main package that includes all compression algorithms

### Key Design Patterns

1. **Native Interop Pattern**: The library uses P/Invoke to call native LZ4/ZStandard libraries
   - Native methods defined in `Raw/NativeMethods.cs`
   - High-level APIs wrap native calls with safe C# interfaces

2. **Streaming API Design**: Three levels of APIs
   - **Simple API**: `byte[]` to `byte[]` conversion (e.g., `LZ4.Compress()`)
   - **Low-level Streaming**: `LZ4Encoder`/`LZ4Decoder` for non-allocating streaming
   - **High-level Streaming**: `CompressAsync`/`DecompressAsync` with parallel processing support

3. **Parallel Processing**: Automatic parallelization for large data (>1MB)
   - Uses `Environment.ProcessorCount` by default
   - Configurable via `maxDegreeOfParallelism` parameter
   - Only works with `BlockIndependent` mode for decompression

### Native Library Management
- Native libraries stored in submodules: `/lz4` and `/zstd`
- Build scripts in `.github/workflows/build-native-*.yaml` compile native libraries
- Platform detection and library loading handled automatically at runtime

### Testing Strategy
- Unit tests in `tests/NativeCompressions.Tests/` using xUnit and FluentAssertions
- Benchmarks in `sandbox/Benchmark/` using BenchmarkDotNet
- Profiling projects in `sandbox/Profiling/` for performance analysis