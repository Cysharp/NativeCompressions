# Catalyst Phase 2: LZ4 ARM64 through NuGet

This standalone .NET 10 app consumes packages from a local feed. It has no direct
Core DLL reference or handwritten NativeReference. It stays outside the root
solution so ordinary Windows/Linux builds do not require Apple workloads.

## CI and reproduction

`.github/workflows/catalyst.yaml` runs on changes pushed to the `catalyst` branch.
Commit/push only when explicitly requested. Creating these files does not run CI.
The job pins SDK/workload set 10.0.401 and Xcode 26.6 on macos-26 ARM64.

On a Mac, from this directory:

```sh
export DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer
dotnet workload install ios maccatalyst --version 10.0.401
bash build-native.sh
bash pack-local.sh
bash test-packages.sh
```

`build-native.sh` uses the checked-out LZ4 submodule, the macOS SDK,
arm64-apple-ios15.0-macabi and XXH_NAMESPACE=LZ4_. It checks every object's
MACCATALYST platform, archive architecture and exported symbols. Native evidence
remains in `artifacts/catalyst-phase1/native/`.

`pack-local.sh` copies that fresh archive into the production Runtime directory,
packs the real Core, every LZ4 Runtime project and the LZ4 meta project, then packs
`CatalystSmoke.Middle`. All packages use the development-only version
`0.0.0-catalyst-phase2`; nothing is published. The feed is under
`artifacts/catalyst-phase2/feed/`. Each invocation creates a new package cache,
and source mapping restricts NativeCompressions/CatalystSmoke packages to that
feed. This prevents reuse of previously restored or public packages.

Core is packed with netstandard2.1 and net10.0-maccatalyst so the test can detect
selection of a generic DLL. Metadata-only packages are restored at netstandard2.1
and packed with --no-build. This preserves their production dependency graph
without traversing net11.0 under SDK 10 or requiring unnecessary Core builds.
The TFM override is never passed to the app or SDK-generated linker projects.
These intentionally scoped packages are test inputs, not release packages.

`test-packages.sh` builds and runs four separate configurations:

| Mode | App reference |
| --- | --- |
| direct | Core + maccatalyst-arm64 Runtime |
| meta | NativeCompressions.LZ4 |
| transitive | CatalystSmoke.Middle -> NativeCompressions.LZ4 |
| duplicate | All three paths together |

The middle library exposes an LZ4 version call, exercised by the last two modes.
Its dependency and buildTransitive assets must propagate through NuGet to the
app. Class libraries do not embed another native archive; the final Exe consumes
one NativeReference. Each mode asserts exactly one static Catalyst ARM64 reference.
`verify-assets.py` also checks that compile/runtime Core DLLs are Catalyst assets
and that the archive path is inside the restored Catalyst package. The iOS Runtime packages contain empty `_._` native groups for both Catalyst
RIDs to prevent NuGet from selecting iOS archives by RID fallback. The source
marker is `src/NativeCompressions.LZ4.Runtime/packaging/_._`; only PackagePath
places it in the two Catalyst native folders inside each iOS NuGet package. The Apple SDK
can link resolved native assets independently of explicit NativeReference items,
so both lists must exclude iOS archives. Windows restore tests verify Catalyst
ARM64/x64 selection and preserve the existing iOS ARM64/x64 assets.


## What a passing run proves

For each mode: package restore and asset selection -> static linking -> ad-hoc
signature verification -> LaunchServices launch -> native LZ4 version call ->
64 KiB compression/decompression -> matching length and every byte -> a result
with this run's unique ID and app termination within 90 seconds. No ForceLoad,
App Store credentials or publication is required.

The bundle name follows ApplicationTitle. The script discovers exactly one .app
under each mode's bin directory. NuGet packages, environment/native evidence,
per-mode assets, native references, binlogs, signature logs, launch logs and result
JSON are uploaded as `catalyst-phase2-arm64`, including after failures.

Windows can run the packaged condition tests without Apple workloads:

```powershell
python verify-targets.py /path/to/feed /path/to/evidence
```

These tests cover inactive platforms/RIDs, outer builds, Universal inner-RID
selection, library exclusion and explicit failure on missing archives. They do
not prove Catalyst execution. Phase 2 remains CI-pending until all four modes
pass. Actual x64/Universal builds, other libraries, release packaging and publish
validation belong to later phases in the implementation plan.
