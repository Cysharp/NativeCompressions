"""Fail if restore selected a generic Core DLL or an iOS/macOS native asset."""
import json
import shutil
import sys
from pathlib import Path

output, evidence = map(Path, sys.argv[1:])
files = list(output.rglob("project.assets.json"))
assert len(files) == 1, files
assets = json.loads(files[0].read_text(encoding="utf-8"))
shutil.copy2(files[0], evidence / "project.assets.json")
targets = [v for k, v in assets["targets"].items() if k.endswith("/maccatalyst-arm64")]
assert len(targets) == 1, assets["targets"].keys()
target = targets[0]
core = target["NativeCompressions.LZ4.Core/0.0.0-catalyst-phase2"]
for kind in ("compile", "runtime"):
    dlls = [p for p in core[kind] if p.endswith(".dll")]
    assert len(dlls) == 1 and dlls[0].startswith("lib/net10.0-maccatalyst"), dlls
native = [p for lib in target.values() for p in lib.get("native", {}) if p.endswith((".a", ".dylib"))]
assert "runtimes/maccatalyst-arm64/native/liblz4.a" in native, native
# RID fallback may expose iOS assets from the existing aggregate packages.
# The actual static linker input must come only from the Catalyst package.
(evidence / "native-asset-candidates.json").write_text(json.dumps(native, indent=2), encoding="utf-8")
references = list(output.rglob("native-references.txt"))
assert len(references) == 1, references
lines = references[0].read_text(encoding="utf-8-sig").splitlines()
assert len(lines) == 1, lines
archive, kind = lines[0].split("|")
assert kind == "Static" and "/nativecompressions.lz4.runtime.maccatalyst-arm64/" in archive.replace("\\", "/").lower(), lines
assert Path(archive).is_file(), archive
shutil.copy2(references[0], evidence / "native-references.txt")
print("Catalyst Core DLL and native asset selection verified.")
