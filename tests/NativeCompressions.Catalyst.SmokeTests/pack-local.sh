#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root/tests/NativeCompressions.Catalyst.SmokeTests"
output="$root/artifacts/catalyst-phase2"
version=0.0.0-catalyst-phase2
mkdir -p "$output/feed" "$output/evidence"
# Always package the archive freshly built from this checkout in CI.
for arch in arm64 x64; do
  cp "$root/artifacts/catalyst-phase3/native/$arch/liblz4.a" "$root/src/NativeCompressions.LZ4.Runtime/runtimes/maccatalyst-$arch/native/liblz4.a"
done
# Scope the SDK 10 TFM override to standalone library/pack invocations, never the app.
# Preserve quotes for MSBuild; %3B would escape the list separator and create one TFM.
dotnet pack "$root/src/NativeCompressions.LZ4.Core/NativeCompressions.LZ4.Core.csproj" \
  -c Release '-p:TargetFrameworks="netstandard2.1;net10.0-maccatalyst"' -p:PackageVersion="$version" \
  -p:ArtifactsPath="$output/core" -o "$output/feed" -bl:"$output/evidence/core-pack.binlog"
# These packages contain only native files or dependencies. Restore their real
# project graph at netstandard2.1, then pack without compiling its references.
meta="$root/src/NativeCompressions.LZ4.csproj"
dotnet restore "$meta" -p:TargetFrameworks=netstandard2.1 -p:ArtifactsPath="$output/pack" -p:PackageVersion="$version"
for project in "$root"/src/NativeCompressions.LZ4.Runtime/*.csproj "$meta"; do
  dotnet pack "$project" -c Release --no-build -p:TargetFrameworks=netstandard2.1 \
    -p:ArtifactsPath="$output/pack" -p:PackageVersion="$version" -o "$output/feed"
done
# Map all NativeCompressions packages exclusively to our feed; no public fallback.
cat > "$output/NuGet.Config" <<EOF
<configuration>
  <packageSources><clear /><add key="local" value="$output/feed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="NativeCompressions.*" /><package pattern="CatalystSmoke.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
EOF
cache="$(mktemp -d "$output/packages.XXXXXX")"
printf '%s\n' "$cache" > "$output/package-cache-path.txt"
dotnet pack Middle/CatalystSmoke.Middle.csproj -c Release -p:PackageVersion="$version" \
  -p:ArtifactsPath="$output/middle" -p:RestoreConfigFile="$output/NuGet.Config" \
  -p:RestorePackagesPath="$cache" -o "$output/feed"
python3 verify-packages.py "$output/feed"
for arch in arm64 x64; do
  python3 verify-targets.py "$output/feed" "$output/target-tests/$arch" "$arch" 2>&1 | tee "$output/evidence/targets-$arch.log"
done
