#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
# Select the smoke app's pinned SDK, even when invoked from another directory.
cd "$root/tests/NativeCompressions.Catalyst.SmokeTests"
mkdir -p "$root/artifacts/catalyst-phase1/evidence"
# This global override belongs only to the Core library build, never the app build.
dotnet build "$root/src/NativeCompressions.LZ4.Core/NativeCompressions.LZ4.Core.csproj" \
  -c Release -f net10.0-maccatalyst -p:TargetFrameworks=net10.0-maccatalyst \
  -p:ArtifactsPath="$root/artifacts/catalyst-phase1/core-build" \
  -o "$root/artifacts/catalyst-phase1/core" \
  -bl:"$root/artifacts/catalyst-phase1/evidence/core-build.binlog"
test -f "$root/artifacts/catalyst-phase1/core/NativeCompressions.LZ4.Core.dll"
