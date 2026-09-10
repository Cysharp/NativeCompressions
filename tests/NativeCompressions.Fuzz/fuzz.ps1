# Runs one fuzz target under libFuzzer via SharpFuzz.
#
# Prerequisites (once):
#   dotnet tool install --global SharpFuzz.CommandLine
#   download libfuzzer-dotnet-windows.exe from https://github.com/Metalnem/libfuzzer-dotnet/releases
#   and place it at tests/NativeCompressions.Fuzz/tools/ (or pass -LibFuzzer)
#
# Usage:
#   ./fuzz.ps1 -Target decompress
#   ./fuzz.ps1 -Target roundtrip -MaxLen 4096 -Jobs 4
#
# Findings are written to findings/<target>/ as crash-* / timeout-* / oom-* files.
# Replay one with:  dotnet artifacts/bin/NativeCompressions.Fuzz/release/NativeCompressions.Fuzz.dll --run <target> <file>

param(
    [ValidateSet("decompress", "decoder", "stream", "decompress-async", "roundtrip", "dictionary", "train",
                 "lz4-decompress", "lz4-decoder", "lz4-stream", "lz4-decompress-async", "lz4-roundtrip", "lz4-dictionary")]
    [string]$Target = "decompress",
    [string]$LibFuzzer = "$PSScriptRoot/tools/libfuzzer-dotnet-windows.exe",
    [int]$MaxLen = 65536,
    [int]$Timeout = 10,
    [int]$RssLimitMb = 4096,
    [int]$Jobs = 1,
    [int]$MaxTotalTimeSec = 0
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command sharpfuzz -ErrorAction SilentlyContinue)) {
    throw "sharpfuzz tool not found. Run: dotnet tool install --global SharpFuzz.CommandLine"
}
if (-not (Test-Path $LibFuzzer)) {
    throw "libfuzzer-dotnet not found at $LibFuzzer. Download it from https://github.com/Metalnem/libfuzzer-dotnet/releases"
}

$root = Resolve-Path "$PSScriptRoot/../.."
$bin = "$root/artifacts/bin/NativeCompressions.Fuzz/release"
$instrumented = "$bin/NativeCompressions.Zstandard.Core.dll"

# The instrumented assembly is newer than its source, so an incremental build would keep it. Remove it first.
if (Test-Path $instrumented) { Remove-Item $instrumented }
dotnet build "$PSScriptRoot/NativeCompressions.Fuzz.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# generate seeds before instrumenting so the corpus generator runs against the plain assembly
$corpus = "$PSScriptRoot/corpus/$Target"
if (-not (Test-Path $corpus)) {
    dotnet "$bin/NativeCompressions.Fuzz.dll" --generate-corpus "$PSScriptRoot/corpus"
    if ($LASTEXITCODE -ne 0) { throw "corpus generation failed" }
}

sharpfuzz $instrumented
if ($LASTEXITCODE -ne 0) { throw "instrumentation failed" }

$findings = "$PSScriptRoot/findings/$Target"
New-Item -ItemType Directory -Force $findings | Out-Null

$env:NATIVECOMPRESSIONS_FUZZ_TARGET = $Target

$fuzzerArgs = @(
    "--target_path=dotnet",
    "--target_arg=$bin/NativeCompressions.Fuzz.dll",
    "-max_len=$MaxLen",
    "-timeout=$Timeout",
    "-rss_limit_mb=$RssLimitMb",
    "-artifact_prefix=$findings/",
    "-print_final_stats=1"
)
if ($Jobs -gt 1) { $fuzzerArgs += "-jobs=$Jobs"; $fuzzerArgs += "-workers=$Jobs" }
if ($MaxTotalTimeSec -gt 0) { $fuzzerArgs += "-max_total_time=$MaxTotalTimeSec" }
$fuzzerArgs += $corpus

& $LibFuzzer @fuzzerArgs
