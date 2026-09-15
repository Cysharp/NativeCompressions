#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root/tests/NativeCompressions.Catalyst.SmokeTests"
output="$root/artifacts/catalyst-phase2"
cache="$(cat "$output/package-cache-path.txt")"
arch="${CATALYST_ARCH:-arm64}"
for mode in direct meta transitive duplicate; do
  mkdir -p "$output/evidence/$mode"
  dotnet build -c Release -p:SmokeReferenceMode="$mode" -p:SmokeArchitecture="$arch" \
    -p:ArtifactsPath="$output/$mode" \
    -p:RestoreConfigFile="$output/NuGet.Config" -p:RestorePackagesPath="$cache" \
    -bl:"$output/evidence/$mode/app-build.binlog" \
    2>&1 | tee "$output/evidence/$mode/app-build.log"
  python3 verify-assets.py "$output/$mode" "$output/evidence/$mode" "$arch"
  app_root="$output/$mode/bin"
  evidence="$output/evidence/$mode"
  mkdir -p "$evidence"
  if [ ! -d "$app_root" ]; then
    echo "::error::App build output directory does not exist: $app_root"
    exit 1
  fi
  # Bundle names follow ApplicationTitle and need not match AssemblyName.
  find "$app_root" -type d -name '*.app' -prune > "$evidence/app-paths.txt"
  app_count="$(wc -l < "$evidence/app-paths.txt" | tr -d ' ')"
  echo "Found $app_count app bundle(s) under $app_root:"
  cat "$evidence/app-paths.txt"
  if [ "$app_count" != 1 ]; then
    echo "::error::Expected exactly one app bundle; found $app_count. See app-paths.txt."
    exit 1
  fi
  app="$(cat "$evidence/app-paths.txt")"
  echo "Verifying signature: $app"
  if ! codesign --verify --deep --strict --verbose=2 "$app" > "$evidence/codesign.log" 2>&1; then
    cat "$evidence/codesign.log"
    echo "::error::App signature verification failed: $app"
    exit 1
  fi
  cat "$evidence/codesign.log"
  echo "Launching Catalyst LZ4 smoke test: $app"
  python3 -u run-app.py "$app" "$evidence" "$arch" 2>&1 | tee "$evidence/run-app.log"
done
