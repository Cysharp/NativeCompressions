# SmokeNative

Checks that the built native libraries (`.dll` / `.so` / `.dylib`) work on their own, without NativeCompressions.

The project references neither NativeCompressions Core nor the Runtime packages. It loads each library with plain `NativeLibrary.Load(absolutePath)` and calls it through this project's own P/Invoke declarations in `NativeMethods.cs`. A result therefore depends only on the native binary, not on the managed wrapper or on packaging.

## Checks

| Library | Checks |
| --- | --- |
| all | load (architecture, OS/glibc version, dependent libraries), dependencies (where each loaded module comes from) |
| lz4 | version, block / HC block / frame round trips |
| zstd | version, round trip, multithreaded round trip (`nbWorkers=2`, needs a `ZSTD_MULTITHREAD` build) |
| openzl | encoding version, round trips through the zstd and lz4 graphs |

Every round trip runs on empty data, 64KB of text and 4MB of mixed data, and compares the bytes for an exact match.

These precautions stop the test environment from hiding problems:

- **Flat layout**: the repo's `runtimes/<rid>/native` files are copied into a single directory, `artifacts/smoke-native/<rid>`. This is the same layout an application gets after a NuGet install.
- **One process per library**: an already loaded library must not satisfy another library's dependency. For example, an app that only uses OpenZL never loads liblz4 first.
- **Where modules come from**: every lz4/zstd/openzl native module in the process must be one of the files given to the smoke run. This catches a dependency resolved to the OS's `/usr/lib/.../liblz4.so.1`, a Homebrew dylib, or any other copy.
- **Windows PATH**: `PATH` is reduced to the system directories. Otherwise DLLs on the developer's `PATH` (for example Git's `mingw64\bin`) hide a missing dependency. When a load fails, the PE import table is listed along with the DLLs that could not be found.

## Usage

```bash
# Repo binaries for the current RID
dotnet run --project sandbox/SmokeNative -c Release

# Only some of the libraries
dotnet run --project sandbox/SmokeNative -c Release -- --libs lz4,zstd

# Another RID on the same machine (for example osx-x64 under Rosetta)
dotnet run --project sandbox/SmokeNative -c Release -- --rid osx-x64

# A flat directory (downloaded CI artifact, app output, ...)
dotnet run --project sandbox/SmokeNative -c Release -- --dir ./native --libs lz4

# Individual files
dotnet run --project sandbox/SmokeNative -c Release -- --zstd ./libzstd.so
```

The exit code is 0 when every check passes, 1 when any check fails, and 2 for invalid arguments.

## CI

`build-debug.yaml` runs the smoke against the committed binaries after the unit tests, on linux / win / osx × x64 / arm64. Each build-native-* workflow calls build-debug with the generated commit after creating its update PR, so newly built binaries go through the same smoke.

## Out of scope

- Static libraries (`.a`) for iOS and Mac Catalyst cannot be loaded with `NativeLibrary.Load`. Use the app-linking check in `.github/docs/plans/mac-catalyst-support.md`.
- Android `.so` files would need a device or an emulator.
- Resolving RIDs from NuGet packages and copying files into the app output. Those belong to packaging.
