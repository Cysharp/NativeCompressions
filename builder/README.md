# Native Library Builder

* zstd
* lz4

# Extensions to place

Native Binaries are used for NuGet packaging.

* Linux, Android: .so
* macOS: .dylib
* iOS: .a
* Windows: .dll

# zstd

Building [zstd](https://github.com/facebook/zstd) for following environment.

OS | Architecture | Build Env| Builder | Build Script | CI
---- | ---- | ---- | ---- | ---- | ----
Android | armeabi-v7a | Docker | CMake | [builder/zstd/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/zstd/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Android | arm64-v8a   | Docker | CMake | [builder/zstd/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/zstd/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Android | x86         | Docker | CMake | [builder/zstd/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/zstd/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Android | x86_64      | Docker | CMake | [builder/zstd/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/zstd/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
iOS     | arm64 | Intel Mac <br/>Apple Silicon Mac | make | [builder/zstd/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/zstd/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Linux   | arm64 | Docker | make | [builder/zstd/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/zstd/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Linux   | x64   | Docker | make | [builder/zstd/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/zstd/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
macOS   | arm64 | Intel Mac <br/>Apple Silicon Mac | make | [builder/zstd/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/zstd/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
macOS   | x64   | Intel Mac <br/>Apple Silicon Mac | make | [builder/zstd/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/zstd/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Windows | arm64 | Windows | CMake | [builder/zstd/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/zstd/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Windows | x64   | Windows | CMake | [builder/zstd/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/zstd/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Windows | x86   | Windows | CMake | [builder/zstd/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/zstd/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)

## lz4

OS | Architecture | Build Env| Builder | Build Script | CI
---- | ---- | ---- | ---- | ---- | ----
Android | armeabi-v7a | Docker | make | [builder/lz4/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/lz4/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Android | arm64-v8a   | Docker | make | [builder/lz4/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/lz4/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Android | x86         | Docker | make | [builder/lz4/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/lz4/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Android | x86_64      | Docker | make | [builder/lz4/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/lz4/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
iOS     | arm64 | Intel Mac <br/>Apple Silicon Mac | make | [builder/lz4/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/lz4/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Linux   | arm64 | Docker | make | [builder/lz4/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/lz4/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Linux   | x64   | Docker | make | [builder/lz4/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/lz4/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
macOS   | arm64 | Intel Mac <br/>Apple Silicon Mac | make | [builder/lz4/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/lz4/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
macOS   | x64   | Intel Mac <br/>Apple Silicon Mac | make | [builder/lz4/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/lz4/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Windows | arm64 | Windows | CMake | [builder/lz4/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/lz4/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Windows | x64   | Windows | CMake | [builder/lz4/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/lz4/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
Windows | x86   | Windows | CMake | [builder/lz4/core](https://github.com/Cysharp/NativeCompressions/tree/master/builder/lz4/core) | [GitHub Actions](https://github.com/Cysharp/NativeCompressions/actions/workflows/build-native.yaml)
