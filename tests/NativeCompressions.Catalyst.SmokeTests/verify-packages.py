"""Check the actual local feed, including production meta-package dependencies."""
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import ZipFile

feed = Path(sys.argv[1])
version = "0.0.0-catalyst-phase2"

def package(name):
    return ZipFile(feed / f"{name}.{version}.nupkg")

def dependencies(name):
    with package(name) as archive:
        root = ET.fromstring(archive.read(f"{name}.nuspec"))
    groups = root.findall(".//{*}dependencies/{*}group")
    assert groups, f"No dependency groups: {name}"
    return {item.attrib["id"] for group in groups for item in group}

for arch in ("arm64", "x64"):
    runtime = f"NativeCompressions.LZ4.Runtime.maccatalyst-{arch}"
    with package(runtime) as archive:
        native = f"runtimes/maccatalyst-{arch}/native/liblz4.a"
        targets = f"buildTransitive/{runtime}.targets"
        assert native in archive.namelist() and targets in archive.namelist()
        assert archive.read(native).startswith(b"!<arch>\n")
        assert len([n for n in archive.namelist() if n.endswith(".a")]) == 1
        assert archive.read(native) == (Path(__file__).resolve().parents[2] / "src" / "NativeCompressions.LZ4.Runtime" / native).read_bytes()
    assert runtime in dependencies("NativeCompressions.LZ4.Runtime")
with package("NativeCompressions.LZ4.Core") as archive:
    assert "lib/netstandard2.1/NativeCompressions.LZ4.Core.dll" in archive.namelist()
    assert any(n.startswith("lib/net10.0-maccatalyst") and n.endswith("NativeCompressions.LZ4.Core.dll") for n in archive.namelist())
assert runtime in dependencies("NativeCompressions.LZ4.Runtime")
assert {"NativeCompressions.LZ4.Core", "NativeCompressions.LZ4.Runtime"} <= dependencies("NativeCompressions.LZ4")
assert "NativeCompressions.LZ4" in dependencies("CatalystSmoke.Middle")
print("Local NuGet files and dependency groups verified.")
