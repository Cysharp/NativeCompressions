#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root/sandbox/SmokeCatalyst"
output="$root/artifacts/catalyst-phase2"
version=0.0.0-catalyst-phase2
mkdir -p "$output/feed" "$output/evidence"
# Always package the archive freshly built from this checkout in CI.
for arch in arm64 x64; do
  cp "$root/artifacts/catalyst-phase3/native/$arch/liblz4.a" "$root/src/NativeCompressions.LZ4.Runtime/runtimes/maccatalyst-$arch/native/liblz4.a"
  mkdir -p "$root/src/NativeCompressions.Zstandard.Runtime/runtimes/maccatalyst-$arch/native"
  cp "$root/artifacts/catalyst-zstandard/$arch/libzstd.a" "$root/src/NativeCompressions.Zstandard.Runtime/runtimes/maccatalyst-$arch/native/libzstd.a"
  mkdir -p "$root/src/NativeCompressions.OpenZL.Runtime/runtimes/maccatalyst-$arch/native"
  cp "$root/artifacts/catalyst-openzl/$arch/libopenzl.a" "$root/src/NativeCompressions.OpenZL.Runtime/runtimes/maccatalyst-$arch/native/libopenzl.a"
done
for library in LZ4 Zstandard OpenZL; do
  # Enable packing only for this local feed, including unpublished OpenZL projects.
  # Scope the SDK 10 TFM override to standalone library/pack invocations, never the app.
  # Preserve quotes for MSBuild; %3B would escape the list separator and create one TFM.
  dotnet pack "$root/src/NativeCompressions.$library.Core/NativeCompressions.$library.Core.csproj" \
    -p:IsPackable=true -c Release '-p:TargetFrameworks="netstandard2.1;net10.0-maccatalyst"' -p:PackageVersion="$version" \
    -p:ArtifactsPath="$output/core/$library" -o "$output/feed" -bl:"$output/evidence/core-$library-pack.binlog"
  # These packages contain only native files or dependencies. Restore their real
  # project graph at netstandard2.1, then pack without compiling its references.
  meta="$root/src/NativeCompressions.$library.csproj"
  dotnet restore "$meta" -p:TargetFrameworks=netstandard2.1 -p:ArtifactsPath="$output/pack" -p:PackageVersion="$version"
  for project in "$root"/src/NativeCompressions.$library.Runtime/*.csproj "$meta"; do
    # OpenZL has no supported 32-bit Android binary.
    if [[ "$project" = */NativeCompressions.OpenZL.Runtime.android-arm.csproj ]]; then continue; fi
    dotnet pack "$project" -p:IsPackable=true -c Release --no-build -p:TargetFrameworks=netstandard2.1 \
      -p:ArtifactsPath="$output/pack" -p:PackageVersion="$version" -o "$output/feed"
  done
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
dotnet build Tools/CatalystSmoke.Tools.csproj -c Release -o "$output/tools"
dotnet "$output/tools/CatalystSmoke.Tools.dll" self-test
dotnet "$output/tools/CatalystSmoke.Tools.dll" verify-packages "$output/feed" "$root"
for arch in arm64 x64; do
  dotnet "$output/tools/CatalystSmoke.Tools.dll" verify-targets "$output/feed" "$output/target-tests/$arch" "$arch" 2>&1 | tee "$output/evidence/targets-$arch.log"
done
