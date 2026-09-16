#!/usr/bin/env bash
set -euo pipefail
test "$(uname -s)" = Darwin
root="$(cd "$(dirname "$0")/../.." && pwd)"
arch="${CATALYST_ARCH:-arm64}"
case "$arch" in
  arm64) clang_arch=arm64 ;;
  x64) clang_arch=x86_64 ;;
  *) echo "Unsupported Catalyst architecture: $arch" >&2; exit 1 ;;
esac
output="$root/artifacts/catalyst-zstandard/$arch"
mkdir -p "$output"
sdk="$(xcrun --sdk macosx --show-sdk-path)"
flags="-O3 -target $clang_arch-apple-ios15.0-macabi -isysroot $sdk"
# %-mt enables the same ZSTD_MULTITHREAD setting as lib-mt, but builds only .a.
# No LTO initially: keep Mach-O objects inspectable and avoid bitcode toolchain coupling.
make -C "$root/zstd/lib" libzstd.a-mt \
  CC="$(xcrun --find clang)" AR="$(xcrun --find ar)" \
  CFLAGS="$flags" ASFLAGS="$flags" DEBUGFLAGS="" BUILD_DIR="$output/obj" \
  -j "$(sysctl -n hw.ncpu)"
cp "$root/zstd/lib/libzstd.a" "$output/libzstd.a"
file "$output/libzstd.a"
lipo -info "$output/libzstd.a"
test "$(lipo -archs "$output/libzstd.a")" = "$clang_arch"
for object in "$output"/obj/static/*.o; do
  xcrun vtool -show-build "$object" > "$object.platform.txt"
  grep -q MACCATALYST "$object.platform.txt"
done
xcrun nm -gU "$output/libzstd.a" > "$output/symbols.txt"
grep ' _ZSTD_versionNumber$' "$output/symbols.txt"
grep ' _ZSTD_compress2$' "$output/symbols.txt"
grep ' _ZSTDMT_createCCtx' "$output/symbols.txt"
{
  git -C "$root" rev-parse HEAD
  git -C "$root/zstd" rev-parse HEAD
  xcodebuild -version
  shasum -a 256 "$output/libzstd.a"
} > "$output/provenance.txt"
# This native probe also rejects a build without working multithread support.
xcrun --sdk macosx clang -target "$clang_arch-apple-ios15.0-macabi" \
  -isysroot "$sdk" -O3 -I "$root/zstd/lib" \
  "$root/sandbox/SmokeCatalyst/zstandard-native-smoke.c" "$output/libzstd.a" \
  -pthread -o "$output/zstandard-native-smoke"
codesign --force --sign - "$output/zstandard-native-smoke"
"$output/zstandard-native-smoke" | tee "$output/result.txt"
