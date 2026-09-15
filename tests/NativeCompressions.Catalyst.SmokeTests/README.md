# Catalyst Phase 1: LZ4 ARM64

This standalone .NET 10 app checks the LZ4 Core ProjectReference with an explicit
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
dotnet build -c Release -p:TargetFrameworks=net10.0-maccatalyst
python3 run-app.py /absolute/path/to/CatalystSmoke.app /absolute/path/to/results
```

The TFM override scopes this .NET 10 probe's restore to the Catalyst target of
Core, avoiding its unrelated net11.0 targets. It is not an OS-skipping flag and
does not change default package targets. The existing SDK-driven MACCATALYST
symbol selects `__Internal` in both generated code and the bindgen recipe.

No ForceLoad is enabled initially. If link/run testing reveals missing symbols,
inspect the native linker output and add the smallest justified retention rule;
do not treat a successful app build as proof of successful P/Invoke.

Phase 1 is only complete after a successful CI run is recorded in the plan.
Phase 7 will expand this development probe into maintained runtime validation.
