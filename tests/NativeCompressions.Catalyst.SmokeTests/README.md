# Catalyst Phase 1: LZ4 ARM64

This standalone .NET 10 app checks the separately built LZ4 Core DLL with an explicit
Catalyst static NativeReference. NuGet integration is Phase 2. It is intentionally
outside the root solution so ordinary Windows/Linux builds do not need Apple workloads.

## Run on CI

`.github/workflows/catalyst.yaml` runs on changes pushed to the `catalyst`
branch. Commit/push only when explicitly requested by the user. The workflow has
not been executed merely by creating these files. `workflow_dispatch` requires
the workflow to exist on the default branch before it can be used reliably.

The job pins SDK/workload set 10.0.401 and Xcode 26.6, builds the pinned LZ4
submodule without updating it, signs a Catalyst app ad hoc, and launches it via
LaunchServices. It requires no App Store credentials or publication.

Native input: lz4.c, lz4hc.c, lz4frame.c, xxhash.c; target
arm64-apple-ios15.0-macabi; XXH_NAMESPACE=LZ4_. Build outputs are under
`artifacts/catalyst-phase1/`, not the runtime package folders.

The app checks the process platform/architecture, obtains LZ4's version,
compresses 64 KiB, decompresses it and verifies every byte. It atomically writes
JSON containing a unique run ID, version, architecture, success and exception.
The runner requires that result and app termination within 90 seconds.

## Manual macOS reproduction

From this directory with Xcode 26.6 selected and SDK 10.0.401 installed:

```sh
export DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer
dotnet workload install ios maccatalyst --version 10.0.401
bash build-native.sh
bash build-core.sh
dotnet build -c Release
python3 run-app.py "/absolute/path/to/NativeCompressions Catalyst Smoke.app" /absolute/path/to/results
```

build-core.sh builds only net10.0-maccatalyst from the production Core project
and places the DLL under artifacts/catalyst-phase1/core. The app uses an assembly
Reference with HintPath, so its restore cannot traverse Core's net11.0 targets.
AdditionalProperties on ProjectReference did not restrict NuGet restore in this
configuration. The TargetFrameworks override is therefore confined to the separate
Core library command, never passed to the app or its SDK-generated linker projects.
This explicit DLL/native archive arrangement is Phase 1 only; Phase 2 will test
NuGet dependency resolution. Core currently has no runtime PackageReference for
this TFM (PolySharp is a private build-time dependency); reassess that if its
dependencies change. MACCATALYST selects __Internal in generated code and bindgen.
CI saves both core-build.binlog and app-build.binlog for diagnosis.

No ForceLoad is enabled initially. If link/run testing reveals missing symbols,
inspect the native linker output and add the smallest justified retention rule;
do not treat a successful app build as proof of successful P/Invoke.

Phase 1 is only complete after a successful CI run is recorded in the plan.
Phase 7 will expand this development probe into maintained runtime validation.

The .app bundle name follows ApplicationTitle, not AssemblyName. CI discovers
bundles under the managed bin output, requires exactly one, and records candidates
in app-paths.txt. Signature verification and launch diagnostics are saved to
codesign.log, run-app.log and launch.log, with failures also visible in the job log.
