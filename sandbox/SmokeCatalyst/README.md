# Catalyst: LZ4, Zstandard and OpenZL ARM64 / x64 through NuGet

This standalone .NET 10 app consumes packages from a local feed. It has no direct Core DLL reference or handwritten NativeReference. It stays outside the root solution so ordinary Windows/Linux builds do not require Apple workloads.

## CI and reproduction

`.github/workflows/catalyst.yaml` runs on changes pushed to the `catalyst` branch. Commit/push only when explicitly requested. Creating these files does not run CI. The job pins SDK/workload set 10.0.401 and Xcode 26.6 on macos-26 ARM64 and macos-26-intel x64.

On a Mac, from this directory:

```sh
export DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer
dotnet workload install ios maccatalyst --version 10.0.401
for arch in arm64 x64; do
  CATALYST_ARCH="$arch" CATALYST_NATIVE_OUTPUT="$PWD/../../artifacts/catalyst-phase3/native/$arch" bash build-native.sh
done
for arch in arm64 x64; do
  CATALYST_ARCH="$arch" bash build-native-zstandard.sh
  CATALYST_ARCH="$arch" bash build-native-openzl.sh
done
bash pack-local.sh
# Select the architecture of this Mac: arm64 or x64.
CATALYST_ARCH=arm64 bash test-packages.sh
```

`build-native.sh` uses the checked-out LZ4 submodule, the macOS SDK, arm64-apple-ios15.0-macabi / x86_64-apple-ios15.0-macabi and XXH_NAMESPACE=LZ4_. It checks every object's MACCATALYST platform, archive architecture and exported symbols. Native evidence is saved in `artifacts/catalyst-phase3/native/<arch>/`.

`pack-local.sh` copies both architectures of each library into their Runtime directories, packs all three real Core projects, supported Runtime projects and meta projects, then packs `CatalystSmoke.Middle`. All packages use the development-only version `0.0.0-catalyst-phase2`; nothing is published. The feed is under `artifacts/catalyst-phase2/feed/`. Each invocation creates a new package cache, and source mapping restricts NativeCompressions/CatalystSmoke packages to that feed. This prevents reuse of previously restored or public packages.

Core is packed with netstandard2.1 and net10.0-maccatalyst so the test can detect selection of a generic DLL. Metadata-only packages are restored at netstandard2.1 and packed with --no-build. This preserves their production dependency graph without traversing net11.0 under SDK 10 or requiring unnecessary Core builds. The TFM override is never passed to the app or SDK-generated linker projects. These intentionally scoped packages are test inputs, not release packages.

`test-packages.sh` builds and runs four separate configurations:

| Mode | App reference |
| --- | --- |
| direct | Core + Runtime matching CATALYST_ARCH |
| meta | NativeCompressions.LZ4 + NativeCompressions.Zstandard + NativeCompressions.OpenZL |
| transitive | CatalystSmoke.Middle -> all three meta packages |
| duplicate | All three paths together |

The middle library exposes LZ4/Zstandard version calls and the OpenZL encoding version, exercised by the last two modes. Its dependency and buildTransitive assets must propagate through NuGet to the app. Class libraries do not embed another native archive; the final Exe consumes three NativeReferences (one per library). Each mode asserts exactly one static Catalyst reference per library matching the selected RID. The C# tool's `verify-assets` command also checks that compile/runtime Core DLLs are Catalyst assets and that the archive path is inside the restored Catalyst package. The iOS Runtime packages contain empty `_._` native groups for both Catalyst RIDs to prevent NuGet from selecting iOS archives by RID fallback. Each Runtime directory owns its source marker at `packaging/_._`; only PackagePath places it in the two Catalyst native folders inside each iOS NuGet package. The Apple SDK can link resolved native assets independently of explicit NativeReference items, so both lists must exclude iOS archives. Windows restore tests verify Catalyst ARM64/x64 selection and preserve the existing iOS ARM64/x64 assets.


## What a passing run proves

For each mode: package restore and asset selection -> static linking -> ad-hoc signature verification -> LaunchServices launch -> native LZ4/Zstandard version calls -> LZ4 64 KiB and Zstandard 8 MiB two-worker compression/decompression -> matching length and every byte -> a result with this run's unique ID and app termination within 90 seconds. No ForceLoad, App Store credentials or publication is required.

The bundle name follows ApplicationTitle. The script discovers exactly one .app under each mode's bin directory. NuGet packages, environment/native evidence, per-mode assets, native references, binlogs, signature logs, launch logs and result JSON are uploaded as `catalyst-phase3-lz4-zstandard-openzl-<arch>`, including after failures.

Windows can run the packaged condition tests without Apple workloads:

```powershell
dotnet ../../artifacts/catalyst-phase2/tools/CatalystSmoke.Tools.dll verify-targets /path/to/feed /path/to/evidence x64
```

These tests cover inactive platforms/RIDs, outer builds, Universal inner-RID selection, library exclusion and explicit failure on missing archives. They do not prove Catalyst execution. Phase 2 ARM64's four modes passed CI run 34960641568. LZ4 ARM64/x64 NuGet app execution passed run 34962526312 and was reconfirmed in run 35065587857. Zstandard and LZ4 together passed all four reference modes on both CPUs in run 35069803096. Universal apps, other libraries, .NET 11 and publish validation remain separate work in the implementation plan.

The initial x64 archive is from run 34961447150, LZ4 commit `ebb370ca83af193212df4dcbadcc5d87bc0de2f0`, SHA256 `844995d7b7e0392e7b95e3cc103d058bd5a6d40c5908fb7a4dddb998d03530da`. Both CI runners rebuild both native archives before packing; committed binaries are not substituted for those fresh test inputs. The app and launch script both check the expected process architecture in addition to the LZ4 round trip.

## Production native update integration

`build-native-lz4.yaml` builds Catalyst ARM64/x64 using inline Bash, alongside its existing platform jobs. It copies the two required archives into the release package and update PR. Artifact copies use the same file-existence checks as the other platforms. When generated files change, the workflow creates or updates a PR and calls `build-debug.yaml` with that update commit SHA. Unchanged output skips both. This integration remains CI-pending; it adds no Python helper or new workflow.

## C# verification tool

All launch and verification commands are implemented in `Tools/CatalystSmoke.Tools.csproj` (net10.0, no external packages). `pack-local.sh` builds it once, runs the launcher self-tests, then invokes `verify-packages` and `verify-targets`. `test-packages.sh` uses the same DLL for `verify-assets` and `run-app`. Python is not required. The tool's source is excluded from the Catalyst app's compilation.

From the repository root on Windows (no Apple workload required):

```powershell
dotnet build sandbox/SmokeCatalyst/Tools/CatalystSmoke.Tools.csproj -c Release -o artifacts/catalyst-tools
dotnet artifacts/catalyst-tools/CatalystSmoke.Tools.dll self-test
dotnet artifacts/catalyst-tools/CatalystSmoke.Tools.dll verify-targets /path/to/feed /path/to/evidence x64
```

`self-test` uses child .NET processes to check ARM64/x64 results, stale IDs, wrong architectures, reported failures, malformed/missing results, nonzero exits and timeouts. It does not launch a Catalyst app; LaunchServices execution was verified for LZ4 in run 35065587857; the expanded Zstandard app passed on both CPUs in run 35069803096.

## Zstandard integration status

The existing `catalyst.yaml` now builds LZ4, Zstandard and OpenZL archives for ARM64/x64 before packing the local feed. The native Zstandard script compiles for iOS 15 macabi with `libzstd.a-mt`, retaining multithreading, with LTO disabled to avoid LLVM bitcode coupling. C and assembly use the same compiler target. It checks object platforms, CPU architecture and symbols; the matching host also runs the native two-worker round trip. Cross-compiled executables are not run on the wrong CPU.

All three libraries are consumed together in every NuGet reference mode. The C# app checks Zstandard's version and compresses/decompresses 8 MiB with `NbWorkers=2` and a 1 MiB job size, then compares every byte. Errors, including unsupported multithreading, fail the app result. `ZstandardVersion` is included in the result JSON. The tools require all three Catalyst Core DLLs and exactly one package-provided static reference per library, reject iOS/native dylib fallback, and run the same package-target condition and restore tests for all three libraries.

The initial Zstandard Catalyst archives were obtained from successful run [35068961870](https://github.com/Cysharp/NativeCompressions/actions/runs/35068961870) and placed in both Runtime RID directories. Zstandard revision `f8745da6ff1ad1e7bab384bd1f9d742439278e99` matches the checkout. SHA256: ARM64 `eff38e5bb9c296ca294a8bd1b58aa2b54b5130887e41d0d4d980bc4143bf65da`, x64 `1404e772baa5aee3146ae88d2aa80134f8fce07829d56b60f7e66b95a61414f5`. Both native two-worker probes passed with Xcode 26.6, and both real Runtime packages were packed on Windows. CI continues to generate and copy fresh archives before packing. The expanded managed app subsequently passed all four reference modes on both CPUs in run [35069803096](https://github.com/Cysharp/NativeCompressions/actions/runs/35069803096), commit `60c29749b3409763c9838d29155e8a75e8179f20`.

The Zstandard .NET 10 basic NuGet path is complete. OpenZL and three-library simultaneous linking passed Catalyst CI run 35074728793. .NET 11 remains Phase 3 work. Production Zstandard native-update integration is implemented in `build-native-zstd.yaml` and awaits CI validation. Like LZ4, it detects submodule or binary changes on release-triggered or manual builds, creates or updates the release PR, and validates the generated commit SHA. Identical output is skipped. Universal and publish remain subsequent phases.

## OpenZL integration

The smoke workflow builds OpenZL for both architectures after LZ4 and Zstandard, then exercises all three together through the four NuGet paths. OpenZL's high-level C# API is currently disabled; `OpenZLSmoke.cs` uses the existing public P/Invoke binding, checks each error result and performs full-byte round trips with both the Zstandard and LZ4 graphs. The result JSON records `OpenZLEncodingVersion`. This does not enable or promise a high-level OpenZL API.

Upstream `Makefile` and `build-scripts/make/multiconf.make` show that `libopenzl.a` contains C/assembly objects only and excludes the LZ4/Zstandard archives from its members. The Catalyst build uses the top-level LZ4/Zstandard headers and fresh archives rather than building host dependencies under `openzl/deps`. The corresponding OpenZL RID package depends on the matching LZ4 and Zstandard RID packages, so an OpenZL-only Runtime reference brings in all three archives; duplicate references still select each archive once. OpenZL's internal xxHash uses inline definitions. No ForceLoad or C++ runtime is added. CI checks archive platform/architecture, rejects exported dependency symbols or unexpected C++ runtime references, and records undefined symbols for diagnosis.

OpenZL remains non-packable by default. Only `pack-local.sh` explicitly enables `IsPackable` for its development packages, and skips the unsupported Android ARM project. This preserves the repository's no-publication policy for OpenZL. The local feed contains its real dependency graph and the tests check dependency propagation and iOS fallback suppression.

Initial Catalyst `libopenzl.a` files were obtained from successful run [35074728793](https://github.com/Cysharp/NativeCompressions/actions/runs/35074728793), commit `1beb9b06c20045372489b601493c0cd79d80c75e`, and placed in both Runtime RID directories. OpenZL revision `3dceb64867840201fb8f57a29d179995f700c9b8` matches the checkout. SHA256: ARM64 `84d29b81cc2fddf33002b57946c13f35a7b159cf2fa4379f5d68e39cb1432fc7`, x64 `db99dbd9acf032722fae9751464b00fb65b7537d56ae048d4ab728a9346b6c82`. Both CPUs passed all four NuGet reference modes with all three libraries, including OpenZL's Zstandard and LZ4 graphs. Source revisions, archive hashes, all 245 object platforms per CPU, and symbol checks were verified; local Runtime packing preserved the archive bytes. OpenZL / .NET 10 basic support and three-library simultaneous execution are complete. Production OpenZL native-update integration is implemented in `build-native-openzl.yaml` and awaits CI validation. It builds the two Catalyst archives, includes them in the release bundle and Runtime updates, detects submodule or binary differences, reuses an open release PR, and validates the generated commit SHA. New releases and manual runs rebuild; unchanged output skips PR operations. OpenZL uses the checked-in matching LZ4/Zstandard archives as external dependencies and does not regenerate them in this workflow. The no-publication policy for OpenZL is unchanged. .NET 11 remains subsequent work.