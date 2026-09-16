# Catalyst: LZ4 ARM64 / x64 through NuGet

This standalone .NET 10 app consumes packages from a local feed. It has no direct
Core DLL reference or handwritten NativeReference. It stays outside the root
solution so ordinary Windows/Linux builds do not require Apple workloads.

## CI and reproduction

`.github/workflows/catalyst.yaml` runs on changes pushed to the `catalyst` branch.
Commit/push only when explicitly requested. Creating these files does not run CI.
The job pins SDK/workload set 10.0.401 and Xcode 26.6 on macos-26 ARM64 and macos-26-intel x64.

On a Mac, from this directory:

```sh
export DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer
dotnet workload install ios maccatalyst --version 10.0.401
for arch in arm64 x64; do
  CATALYST_ARCH="$arch" CATALYST_NATIVE_OUTPUT="$PWD/../../artifacts/catalyst-phase3/native/$arch" bash build-native.sh
done
bash pack-local.sh
# Select the architecture of this Mac: arm64 or x64.
CATALYST_ARCH=arm64 bash test-packages.sh
```

`build-native.sh` uses the checked-out LZ4 submodule, the macOS SDK,
arm64-apple-ios15.0-macabi / x86_64-apple-ios15.0-macabi and XXH_NAMESPACE=LZ4_. It checks every object's
MACCATALYST platform, archive architecture and exported symbols. Native evidence
is saved in `artifacts/catalyst-phase3/native/<arch>/`.

`pack-local.sh` copies both fresh archives into the production Runtime directory,
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
| direct | Core + Runtime matching CATALYST_ARCH |
| meta | NativeCompressions.LZ4 |
| transitive | CatalystSmoke.Middle -> NativeCompressions.LZ4 |
| duplicate | All three paths together |

The middle library exposes an LZ4 version call, exercised by the last two modes.
Its dependency and buildTransitive assets must propagate through NuGet to the
app. Class libraries do not embed another native archive; the final Exe consumes
one NativeReference. Each mode asserts exactly one static Catalyst reference matching the selected RID.
The C# tool's `verify-assets` command also checks that compile/runtime Core DLLs are Catalyst assets
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
JSON are uploaded as `catalyst-phase3-lz4-<arch>`, including after failures.

Windows can run the packaged condition tests without Apple workloads:

```powershell
dotnet ../../artifacts/catalyst-phase2/tools/CatalystSmoke.Tools.dll verify-targets /path/to/feed /path/to/evidence x64
```

These tests cover inactive platforms/RIDs, outer builds, Universal inner-RID
selection, library exclusion and explicit failure on missing archives. They do
not prove Catalyst execution. Phase 2 ARM64's four modes passed CI run
34960641568. Phase 3 x64 native compilation passed run 34961447150; the new
x64 NuGet app execution remains CI-pending. Universal apps, other libraries,
.NET 11 and publish validation remain separate work in the implementation plan.

The initial x64 archive is from run 34961447150, LZ4 commit
`ebb370ca83af193212df4dcbadcc5d87bc0de2f0`, SHA256
`844995d7b7e0392e7b95e3cc103d058bd5a6d40c5908fb7a4dddb998d03530da`.
Both CI runners rebuild both native archives before packing; committed binaries
are not substituted for those fresh test inputs. The app and launch script both
check the expected process architecture in addition to the LZ4 round trip.

## Production native update integration

`build-native-lz4.yaml` builds Catalyst ARM64/x64 using inline Bash, alongside
its existing platform jobs. It copies the two required archives into the release
package and update PR. Artifact copies use the same file-existence checks as the other platforms.
When generated files change, the workflow creates or updates a PR and calls
`build-debug.yaml` with that update commit SHA. Unchanged output skips both.
This integration remains CI-pending; it adds no Python helper or new workflow.

## C# verification tool

All launch and verification commands are implemented in `Tools/CatalystSmoke.Tools.csproj`
(net10.0, no external packages). `pack-local.sh` builds it once, runs the launcher
self-tests, then invokes `verify-packages` and `verify-targets`. `test-packages.sh`
uses the same DLL for `verify-assets` and `run-app`. Python is not required.
The tool's source is excluded from the Catalyst app's compilation.

From the repository root on Windows (no Apple workload required):

```powershell
dotnet build sandbox/SmokeCatalyst/Tools/CatalystSmoke.Tools.csproj -c Release -o artifacts/catalyst-tools
dotnet artifacts/catalyst-tools/CatalystSmoke.Tools.dll self-test
dotnet artifacts/catalyst-tools/CatalystSmoke.Tools.dll verify-targets /path/to/feed /path/to/evidence x64
```

`self-test` uses child .NET processes to check ARM64/x64 results, stale IDs,
wrong architectures, reported failures, malformed/missing results, nonzero exits
and timeouts. It does not launch a Catalyst app; LaunchServices execution must
still be verified by the macOS CI after this migration.
