#!/usr/bin/env bash
# Compiles liblz4 and libzstd from the submodules to wasm static archives with the Emscripten
# that the wasm-tools workload ships, so the archives match the runtime the .NET SDK relinks.
# Run from any directory. Needs the wasm-tools workload (dotnet workload install wasm-tools).
set -euo pipefail

cd "$(dirname "$0")"
repo="$(cd ../../.. && pwd)"

packs="$(dirname "$(command -v dotnet)")/packs"
sdk="$(ls -d "$packs"/Microsoft.NET.Runtime.Emscripten.*.Sdk.*/* | sort -V | tail -1)"
ver="$(basename "$sdk")"
pack="$(basename "$(dirname "$sdk")")"
em="$(echo "$pack" | sed -E 's/Microsoft.NET.Runtime.Emscripten.(.*).Sdk.*/\1/')"
rid="$(echo "$pack" | sed -E 's/.*\.Sdk\.//')"
tools="$sdk/tools"
node="$packs/Microsoft.NET.Runtime.Emscripten.$em.Node.$rid/$ver/tools"
python="$packs/Microsoft.NET.Runtime.Emscripten.$em.Python.$rid/$ver/tools"
cache="$packs/Microsoft.NET.Runtime.Emscripten.$em.Cache.$rid/$ver/tools/emscripten/cache"

# The same environment that the wasm SDK sets up before it calls emcc.
export EMSDK_PYTHON="$python/python.exe"
[[ -f "$EMSDK_PYTHON" ]] || EMSDK_PYTHON="$python/bin/python3"
export PYTHONUTF8=1 PYTHONHOME=
export EM_CACHE="$cache" EM_FROZEN_CACHE=1
export DOTNET_EMSCRIPTEN_LLVM_ROOT="$tools/bin" DOTNET_EMSCRIPTEN_BINARYEN_ROOT="$tools"
exe=""
[[ "$rid" == win-* ]] && exe=".exe"
export DOTNET_EMSCRIPTEN_NODE_JS="$node/bin/node$exe"
export PATH="$tools/emscripten:$tools/bin:$node/bin:$(dirname "$EMSDK_PYTHON"):$PATH"

echo "emscripten $em at $tools"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# LZ4: the frame API and HC, xxhash under its own prefix so it does not collide with the one in zstd.
mkdir "$work/lz4"
(cd "$work/lz4" && emcc -O3 -DXXH_NAMESPACE=LZ4_ -c "$repo"/lz4/lib/lz4.c "$repo"/lz4/lib/lz4hc.c "$repo"/lz4/lib/lz4frame.c "$repo"/lz4/lib/xxhash.c)
emar rcs lz4.a "$work"/lz4/*.o

# Zstandard: the whole library including the dictionary builder, single threaded, no legacy formats.
mkdir "$work/zstd"
(cd "$work/zstd" && emcc -O3 -DXXH_NAMESPACE=ZSTD_ -DZSTD_LEGACY_SUPPORT=0 -DZSTD_DISABLE_ASM -I"$repo/zstd/lib" \
  -c "$repo"/zstd/lib/common/*.c "$repo"/zstd/lib/compress/*.c "$repo"/zstd/lib/decompress/*.c "$repo"/zstd/lib/dictBuilder/*.c)
emar rcs libzstd.a "$work"/zstd/*.o

ls -la lz4.a libzstd.a
