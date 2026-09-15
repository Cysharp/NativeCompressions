using NativeCompressions.Fuzz;
using SharpFuzz;

NativeLoader.EnsureRegistered();

// Instrumented code writes coverage into Trace.SharedMem, which Fuzzer.Run sets up.
// The corpus and replay modes run outside Fuzzer.Run, so give them a scratch buffer.
unsafe
{
    if (SharpFuzz.Common.Trace.SharedMem == null)
    {
        SharpFuzz.Common.Trace.SharedMem = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(65536);
    }
}

// Usage:
//   NativeCompressions.Fuzz <target>                      run under libFuzzer
//   NativeCompressions.Fuzz <target> <file>...            replay files through a target and report (same as --run)
//   NativeCompressions.Fuzz --run <target> <file>...
//   NativeCompressions.Fuzz --generate-corpus <dir>       write seed inputs for every target to <dir>/<target>/
// The target can also be given by the NATIVECOMPRESSIONS_FUZZ_TARGET environment variable,
// which libfuzzer-dotnet needs because it passes a single --target_arg.

if (args.Length >= 2 && args[0] == "--generate-corpus")
{
    Corpus.Write(args[1]);
    Console.WriteLine($"corpus written to {Path.GetFullPath(args[1])}");
    return 0;
}

var isReplay = (args.Length >= 3 && args[0] == "--run") || (args.Length >= 2 && File.Exists(args[1]));
if (isReplay)
{
    var replayArgs = args[0] == "--run" ? args.Skip(1).ToArray() : args;
    if (!FuzzTargets.All.TryGetValue(replayArgs[0], out var replay))
    {
        return Unknown(replayArgs[0]);
    }

    var failed = 0;
    foreach (var file in replayArgs.Skip(1))
    {
        try
        {
            replay(File.ReadAllBytes(file));
            Console.WriteLine($"ok    {file}");
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"FAIL  {file}: {ex.GetType().Name}: {ex.Message}");
        }
    }
    return failed == 0 ? 0 : 1;
}

var targetName = args.Length >= 1 ? args[0] : Environment.GetEnvironmentVariable("NATIVECOMPRESSIONS_FUZZ_TARGET");
if (string.IsNullOrEmpty(targetName))
{
    Console.Error.WriteLine("target name required. available: " + string.Join(", ", FuzzTargets.All.Keys));
    return 2;
}

if (!FuzzTargets.All.TryGetValue(targetName, out var target))
{
    return Unknown(targetName);
}

Fuzzer.LibFuzzer.Run(span => target(span));
return 0;

static int Unknown(string name)
{
    Console.Error.WriteLine($"unknown target '{name}'. available: " + string.Join(", ", FuzzTargets.All.Keys));
    return 2;
}
