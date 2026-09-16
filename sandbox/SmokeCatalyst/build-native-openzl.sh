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
output="$root/artifacts/catalyst-openzl/$arch"
lz4="$root/artifacts/catalyst-phase3/native/$arch/liblz4.a"
zstd="$root/artifacts/catalyst-zstandard/$arch/libzstd.a"
test -f "$lz4"
test -f "$zstd"
mkdir -p "$output"
sdk="$(xcrun --sdk macosx --show-sdk-path)"
# The upstream static target contains only OpenZL objects. Supply the same headers
# and archives as our Runtime packages; -o prevents host dependency rebuilds.
make -C "$root/openzl" libopenzl.a BUILD_TYPE=OPT \
  CC="$(xcrun --find clang)" AR="$(xcrun --find ar)" \
  MOREFLAGS="-target $clang_arch-apple-ios15.0-macabi -isysroot $sdk" \
  CPPFLAGS="-I. -Iinclude -Isrc -Icpp/include -Icpp/src -I$root/zstd/lib -I$root/lz4/lib -DNDEBUG" \
  LIBZSTD_A="$zstd" LIBLZ4_A="$lz4" -o "$zstd" -o "$lz4" \
  CACHE_ROOT="$output/obj" -j "$(sysctl -n hw.ncpu)"
cp -L "$root/openzl/libopenzl.a" "$output/libopenzl.a"
file "$output/libopenzl.a"
lipo -info "$output/libopenzl.a"
test "$(lipo -archs "$output/libopenzl.a")" = "$clang_arch"
find "$output/obj" -name '*.o' -print0 | while IFS= read -r -d '' object; do
  xcrun vtool -show-build "$object" > "$object.platform.txt"
  grep -q MACCATALYST "$object.platform.txt"
done
xcrun nm -gU "$output/libopenzl.a" > "$output/symbols.txt"
xcrun nm -u "$output/libopenzl.a" > "$output/undefined-symbols.txt"
grep ' _ZL_CCtx_compress$' "$output/symbols.txt"
grep ' _ZL_DCtx_decompress$' "$output/symbols.txt"
# Do not embed competing dependency implementations or introduce a C++ runtime.
if grep -E ' _(ZSTD_|LZ4_|XXH)' "$output/symbols.txt"; then
  echo 'Unexpected bundled LZ4/Zstandard/xxHash symbols' >&2
  exit 1
fi
if grep -E ' (__Z|___cxa|___gxx)' "$output/undefined-symbols.txt"; then
  echo 'Unexpected C++ runtime dependency' >&2
  exit 1
fi
{
  git -C "$root" rev-parse HEAD
  git -C "$root/openzl" rev-parse HEAD
  git -C "$root/lz4" rev-parse HEAD
  git -C "$root/zstd" rev-parse HEAD
  xcodebuild -version
  shasum -a 256 "$output/libopenzl.a" "$lz4" "$zstd"
} > "$output/provenance.txt"
