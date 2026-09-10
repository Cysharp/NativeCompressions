#!/usr/bin/env bash
# Runs one fuzz target under libFuzzer via SharpFuzz on Linux / macOS.
#
# Prerequisites (once):
#   dotnet tool install --global SharpFuzz.CommandLine
#   download libfuzzer-dotnet-ubuntu (or build libfuzzer-dotnet) from https://github.com/Metalnem/libfuzzer-dotnet
#   and place it at tests/NativeCompressions.Fuzz/tools/libfuzzer-dotnet (or set LIBFUZZER_DOTNET)
#
# Usage:
#   ./fuzz.sh decompress
#   MAX_LEN=4096 JOBS=4 ./fuzz.sh roundtrip
set -euo pipefail

TARGET="${1:-decompress}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
BIN="$ROOT/artifacts/bin/NativeCompressions.Fuzz/release"
LIBFUZZER="${LIBFUZZER_DOTNET:-$HERE/tools/libfuzzer-dotnet}"
MAX_LEN="${MAX_LEN:-65536}"
TIMEOUT="${TIMEOUT:-10}"
RSS_LIMIT_MB="${RSS_LIMIT_MB:-4096}"
JOBS="${JOBS:-1}"

command -v sharpfuzz >/dev/null || { echo "sharpfuzz tool not found. Run: dotnet tool install --global SharpFuzz.CommandLine"; exit 1; }
[ -x "$LIBFUZZER" ] || { echo "libfuzzer-dotnet not found at $LIBFUZZER"; exit 1; }

INSTRUMENTED="$BIN/NativeCompressions.Zstandard.Core.dll"
rm -f "$INSTRUMENTED"
dotnet build "$HERE/NativeCompressions.Fuzz.csproj" -c Release

# generate seeds before instrumenting so the corpus generator runs against the plain assembly
CORPUS="$HERE/corpus/$TARGET"
[ -d "$CORPUS" ] || dotnet "$BIN/NativeCompressions.Fuzz.dll" --generate-corpus "$HERE/corpus"

sharpfuzz "$INSTRUMENTED"

FINDINGS="$HERE/findings/$TARGET"
mkdir -p "$FINDINGS"

export NATIVECOMPRESSIONS_FUZZ_TARGET="$TARGET"

ARGS=(--target_path=dotnet "--target_arg=$BIN/NativeCompressions.Fuzz.dll" "-max_len=$MAX_LEN" "-timeout=$TIMEOUT" "-rss_limit_mb=$RSS_LIMIT_MB" "-artifact_prefix=$FINDINGS/" -print_final_stats=1)
if [ "$JOBS" -gt 1 ]; then ARGS+=("-jobs=$JOBS" "-workers=$JOBS"); fi
if [ -n "${MAX_TOTAL_TIME:-}" ]; then ARGS+=("-max_total_time=$MAX_TOTAL_TIME"); fi
ARGS+=("$CORPUS")

exec "$LIBFUZZER" "${ARGS[@]}"
