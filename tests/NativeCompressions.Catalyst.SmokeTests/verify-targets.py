"""Exercise packaged MSBuild conditions without Apple workloads (also on Windows)."""
import json
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from xml.sax.saxutils import quoteattr
from zipfile import ZipFile

feed, output = map(lambda p: Path(p).resolve(), sys.argv[1:])
output.mkdir(parents=True, exist_ok=True)
package = "NativeCompressions.LZ4.Runtime.maccatalyst-arm64"
with ZipFile(feed / f"{package}.0.0.0-catalyst-phase2.nupkg") as archive:
    archive.extractall(output / "package")
targets = output / "package" / "buildTransitive" / f"{package}.targets"
base = dict(TargetFramework="net10.0-maccatalyst", TargetPlatformIdentifier="maccatalyst", RuntimeIdentifier="maccatalyst-arm64", OutputType="Exe")
cases = {
    "arm64": ({}, 1),
    "x64": ({"RuntimeIdentifier": "maccatalyst-x64"}, 0),
    "ios": ({"TargetPlatformIdentifier": "ios", "RuntimeIdentifier": "ios-arm64"}, 0),
    "macos": ({"TargetPlatformIdentifier": "macos", "RuntimeIdentifier": "osx-arm64"}, 0),
    "windows": ({"TargetPlatformIdentifier": "", "RuntimeIdentifier": "win-x64"}, 0),
    "linux": ({"TargetPlatformIdentifier": "", "RuntimeIdentifier": "linux-x64"}, 0),
    "outer": ({"TargetFramework": "", "IsCrossTargetingBuild": "true"}, 0),
    "universal-outer": ({"RuntimeIdentifier": "", "RuntimeIdentifiers": "maccatalyst-arm64;maccatalyst-x64"}, 0),
    "universal-arm64": ({"RuntimeIdentifiers": "maccatalyst-arm64;maccatalyst-x64"}, 1),
    "library": ({"OutputType": "Library"}, 0),
}
for name, (overrides, count) in cases.items():
    props = base | overrides
    project = output / f"{name}.proj"
    project.write_text("<Project><PropertyGroup>" + "".join(f"<{k}>{v}</{k}>" for k, v in props.items()) + "</PropertyGroup><Import Project=" + quoteattr(str(targets)) + " /></Project>", encoding="utf-8")
    result = subprocess.run(["dotnet", "msbuild", str(project), "-nologo", "-v:q", "-t:CheckNativeCompressionsLz4CatalystArm64", "-getItem:NativeReference"], text=True, encoding="utf-8", errors="replace", capture_output=True)
    assert result.returncode == 0, result.stdout + result.stderr
    items = json.loads(result.stdout)["Items"]["NativeReference"]
    assert len(items) == count, (name, items)
    print(f"{name}: {count} NativeReference")
# A broken package must fail instead of silently dropping its NativeReference.
missing = output / "missing" / "buildTransitive"
missing.mkdir(parents=True, exist_ok=True)
(missing / targets.name).write_bytes(targets.read_bytes())
project = output / "missing.proj"
project.write_text((output / "arm64.proj").read_text(encoding="utf-8").replace(str(targets), str(missing / targets.name)), encoding="utf-8")
result = subprocess.run(["dotnet", "msbuild", str(project), "-nologo", "-t:CheckNativeCompressionsLz4CatalystArm64"], text=True, encoding="utf-8", errors="replace", capture_output=True)
assert result.returncode != 0 and "is missing its Catalyst archive" in result.stdout, result.stdout + result.stderr
print("Missing archive: expected explicit failure")

# Restore the real NuGet graph to verify that buildTransitive survives the
# aggregate package's default dependency exclusions (Build,Analyzers).
for mode, ids in {
    "direct": [package],
    "aggregate": ["NativeCompressions.LZ4.Runtime"],
    "duplicate": [package, "NativeCompressions.LZ4.Runtime"],
}.items():
    folder = output / f"restore-{mode}"
    folder.mkdir(exist_ok=True)
    project = folder / "Consumer.csproj"
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.1</TargetFramework><EnableDefaultItems>false</EnableDefaultItems></PropertyGroup><ItemGroup>' + "".join(f'<PackageReference Include="{name}" Version="0.0.0-catalyst-phase2" />' for name in ids) + '</ItemGroup></Project>', encoding="utf-8")
    result = subprocess.run(["dotnet", "restore", str(project), "--source", str(feed), "--packages", str(folder / "cache"), "-p:NuGetAudit=false", f"-p:ArtifactsPath={folder / 'artifacts'}"], text=True, encoding="utf-8", errors="replace", capture_output=True)
    assert result.returncode == 0, result.stdout + result.stderr
    imports = list((folder / "artifacts").rglob("*.nuget.g.targets"))
    assert len(imports) == 1, imports
    assert len([item for item in ET.parse(imports[0]).findall(".//{*}Import") if item.attrib["Project"].endswith(package + ".targets")]) == 1
    probe = folder / "Evaluate.proj"
    props = base | {"TargetFramework": "netstandard2.1"}
    probe.write_text("<Project><PropertyGroup>" + "".join(f"<{k}>{v}</{k}>" for k, v in props.items()) + '</PropertyGroup><Import Project=' + quoteattr(str(imports[0]).replace(".targets", ".props")) + ' /><Import Project=' + quoteattr(str(imports[0])) + ' /></Project>', encoding="utf-8")
    result = subprocess.run(["dotnet", "msbuild", str(probe), "-nologo", "-v:q", "-t:CheckNativeCompressionsLz4CatalystArm64", "-getItem:NativeReference"], text=True, encoding="utf-8", errors="replace", capture_output=True)
    assert result.returncode == 0, result.stdout + result.stderr
    assert len(json.loads(result.stdout)["Items"]["NativeReference"]) == 1
    print(f"NuGet {mode}: one transitive NativeReference")

# Verify NuGet's actual RID fallback, not only explicit NativeReference items.
for rid, expected in {
    "maccatalyst-arm64": ["runtimes/maccatalyst-arm64/native/liblz4.a"],
    "maccatalyst-x64": [],  # x64 Catalyst archive is a later phase.
    "ios-arm64": ["runtimes/ios-arm64/native/liblz4.a"],
    "ios-x64": ["runtimes/ios-x64/native/liblz4.a"],
}.items():
    folder = output / f"rid-{rid}"
    folder.mkdir(exist_ok=True)
    project = folder / "Consumer.csproj"
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.1</TargetFramework><EnableDefaultItems>false</EnableDefaultItems></PropertyGroup><ItemGroup><PackageReference Include="NativeCompressions.LZ4.Runtime" Version="0.0.0-catalyst-phase2" /></ItemGroup></Project>', encoding="utf-8")
    result = subprocess.run(["dotnet", "restore", str(project), "-r", rid, "--source", str(feed), "--packages", str(folder / "cache"), "-p:NuGetAudit=false", f"-p:ArtifactsPath={folder / 'artifacts'}"], text=True, encoding="utf-8", errors="replace", capture_output=True)
    assert result.returncode == 0, result.stdout + result.stderr
    assets = json.loads(next((folder / "artifacts").rglob("project.assets.json")).read_text(encoding="utf-8"))
    target = next(v for k, v in assets["targets"].items() if k.endswith("/" + rid))
    native = [p for lib in target.values() for p in lib.get("native", {}) if p.endswith(".a")]
    assert native == expected, (rid, native)
    print(f"{rid}: native assets {native}")
