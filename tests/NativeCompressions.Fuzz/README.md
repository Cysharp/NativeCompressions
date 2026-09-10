# NativeCompressions.Fuzz

Coverage guided fuzzing of the Zstandard and LZ4 bindings with [SharpFuzz](https://github.com/Metalnem/sharpfuzz) and libFuzzer.
The same targets are replayed by the unit tests (`ZstandardFuzzRegressionTest`) over the seed corpus,
a fixed set of deterministic mutations, and every file under `regressions/`.

## Targets

| Target | What it exercises | Expected exceptions |
|---|---|---|
| `decompress` | `Zstandard.Decompress` (trusted and untrusted), `TryDecompress`, frame inspection, BCL `ZstandardDecoder.TryDecompress` as an oracle | `ZstandardException` |
| `decoder` | `ZstandardDecoder` with input and output chunk sizes taken from the data, multi frame, drain | none (returns `InvalidData`) |
| `stream` | `ZstandardStream` reading from an inner stream that returns small reads | `InvalidOperationException` |
| `decompress-async` | `DecompressAsync` over a multi segment `ReadOnlySequence`, compared with one-shot | `ZstandardException` |
| `roundtrip` | compress with options taken from the data, decompress through every path, both directions against the BCL | none |
| `dictionary` | raw content dictionaries: create, compress, decompress with and without, BCL with the same bytes | `ZstandardException` only when decoding without the dictionary |
| `train` | `ZstandardDictionary.Train` with sample splits taken from the data, then a round trip | `ZstandardException`, `ArgumentException` |
| `lz4-decompress` | `LZ4.Decompress` (trusted and untrusted), span overload, `LZ4Stream` and `TryGetFrameInfo`, all must agree | `LZ4Exception` |
| `lz4-decoder` | `LZ4Decoder` with input and output chunk sizes taken from the data, multi frame, drain | none (returns `InvalidData`) |
| `lz4-stream` | `LZ4Stream` reading from an inner stream that returns small reads | `InvalidOperationException` |
| `lz4-decompress-async` | `DecompressAsync` over a multi segment sequence with parallelism 1 and 2, compared with one-shot | `LZ4Exception` |
| `lz4-roundtrip` | compress with options taken from the data, decompress through every path including block parallel | none |
| `lz4-dictionary` | raw dictionaries with an id: create, compress, decompress with and without, frame header id | `LZ4Exception` only when decoding without the dictionary |

Anything else escaping a target is a finding: a native crash, a hang, an unexpected exception type, or a `FuzzAssertionException` for a broken invariant.
Output is capped at 8 MB per run because small inputs can legally expand into gigabytes.

## Running

Once:

```bash
dotnet tool install --global SharpFuzz.CommandLine
```

Download `libfuzzer-dotnet-windows.exe` (or the Ubuntu binary) from the [libfuzzer-dotnet releases](https://github.com/Metalnem/libfuzzer-dotnet/releases) into `tests/NativeCompressions.Fuzz/tools/`.

Then:

```powershell
./fuzz.ps1 -Target decompress
./fuzz.ps1 -Target roundtrip -MaxLen 4096 -Jobs 4 -MaxTotalTimeSec 600
```

```bash
./fuzz.sh decompress
MAX_LEN=4096 JOBS=4 MAX_TOTAL_TIME=600 ./fuzz.sh roundtrip
```

The script rebuilds, instruments `NativeCompressions.Zstandard.Core.dll` in the fuzz output folder, generates the seed corpus on first use, and starts libFuzzer.
Findings land in `findings/<target>/`.

## Reproducing and keeping a finding

```bash
dotnet artifacts/bin/NativeCompressions.Fuzz/release/NativeCompressions.Fuzz.dll --run decompress findings/decompress/crash-abc123
```

Copy the input to `regressions/<target>/<name>`; it is embedded into the assembly and replayed by the unit tests from then on.

## Notes

- Only the managed binding is instrumented. libzstd itself is not, so coverage feedback comes from the C# paths, while native crashes are still caught by libFuzzer.
- `NATIVECOMPRESSIONS_FUZZ_TARGET` selects the target when no argument is given, because `libfuzzer-dotnet` forwards a single `--target_arg`.
- `NativeCompressions.Fuzz <target> <file>...` replays files without libFuzzer, the same as `--run`.
- Instrumented assemblies need `SharpFuzz.Common.Trace.SharedMem`; `Program` allocates a scratch buffer for the corpus and replay modes so they work on an instrumented build too.
