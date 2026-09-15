#!/usr/bin/env bash
set -euo pipefail
test "$(uname -s)" = Darwin
root="$(cd "$(dirname "$0")/../.." && pwd)"
output="$root/artifacts/catalyst-phase1/native"
mkdir -p "$output"
sdk="$(xcrun --sdk macosx --show-sdk-path)"
# Compile out of tree without changing the pinned submodule or its build products.
for source in lz4 lz4hc lz4frame xxhash; do
  xcrun --sdk macosx clang -target arm64-apple-ios15.0-macabi \
    -isysroot "$sdk" -O3 -DXXH_NAMESPACE=LZ4_ \
    -c "$root/lz4/lib/$source.c" -o "$output/$source.o"
  xcrun vtool -show-build "$output/$source.o" > "$output/$source.platform.txt"
  grep -q MACCATALYST "$output/$source.platform.txt"
done
xcrun libtool -static -o "$output/liblz4.a" "$output/lz4.o" "$output/lz4hc.o" "$output/lz4frame.o" "$output/xxhash.o"
file "$output/liblz4.a"
xcrun lipo -info "$output/liblz4.a"
xcrun nm -gU "$output/liblz4.a" > "$output/symbols.txt"
grep -q ' _LZ4_versionNumber$' "$output/symbols.txt"
grep -q ' _LZ4F_compressFrame$' "$output/symbols.txt"
