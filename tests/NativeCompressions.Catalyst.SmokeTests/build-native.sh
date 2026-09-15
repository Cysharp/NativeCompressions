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
output="${CATALYST_NATIVE_OUTPUT:-$root/artifacts/catalyst-phase1/native}"
mkdir -p "$output"
sdk="$(xcrun --sdk macosx --show-sdk-path)"
# Compile out of tree without changing the pinned submodule or its build products.
for source in lz4 lz4hc lz4frame xxhash; do
  xcrun --sdk macosx clang -target "$clang_arch-apple-ios15.0-macabi" \
    -isysroot "$sdk" -O3 -DXXH_NAMESPACE=LZ4_ \
    -c "$root/lz4/lib/$source.c" -o "$output/$source.o"
  xcrun vtool -show-build "$output/$source.o" > "$output/$source.platform.txt"
  grep -q MACCATALYST "$output/$source.platform.txt"
done
xcrun libtool -static -o "$output/liblz4.a" "$output/lz4.o" "$output/lz4hc.o" "$output/lz4frame.o" "$output/xxhash.o"
file "$output/liblz4.a"
xcrun lipo -info "$output/liblz4.a"
test "$(xcrun lipo -archs "$output/liblz4.a")" = "$clang_arch"
{
  printf 'rid=maccatalyst-%s\n' "$arch"
  git -C "$root" rev-parse HEAD
  git -C "$root/lz4" rev-parse HEAD
  xcodebuild -version
  shasum -a 256 "$output/liblz4.a"
} > "$output/provenance.txt"
xcrun nm -gU "$output/liblz4.a" > "$output/symbols.txt"
grep -q ' _LZ4_versionNumber$' "$output/symbols.txt"
grep -q ' _LZ4F_compressFrame$' "$output/symbols.txt"
