using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using static CatalystSmoke.Tools.Program;

namespace CatalystSmoke.Tools;

static class Launcher
{
    // The app writes this file atomically. Both its ID and CPU must match this run.
    static void Validate(string file, string id, string arch)
    {
        var result = ReadJson(file);
        Require(result["RunId"]?.GetValue<string>() == id && result["Success"]?.GetValue<bool>() == true && result["Architecture"]?.GetValue<string>() == (arch == "arm64" ? "Arm64" : "X64"), $"Catalyst smoke test failed: {result}");
    }

    static async Task Wait(Process process, string file, string id, string arch, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            while (!File.Exists(file))
            {
                Require(!process.HasExited, $"App launcher exited ({(process.HasExited ? process.ExitCode : -1)}) without a result");
                await Task.Delay(100, deadline.Token);
            }
            Validate(file, id, arch);
            await process.WaitForExitAsync(deadline.Token);
            Require(process.ExitCode == 0, $"App launcher failed: {process.ExitCode}");
            Console.WriteLine(File.ReadAllText(file));
        }
        catch (OperationCanceledException) { throw new TimeoutException($"Catalyst app did not finish within {timeout.TotalSeconds} seconds"); }
    }

    internal static async Task Run(string app, string output, string arch)
    {
        Require(OperatingSystem.IsMacOS(), "Catalyst app launching requires macOS");
        app = Path.GetFullPath(app);
        output = Path.GetFullPath(output);
        Require(Directory.Exists(app), $"App bundle not found: {app}");
        Directory.CreateDirectory(output);
        var id = Guid.NewGuid().ToString();
        var result = Path.Combine(output, $"result-{id}.json");
        Console.WriteLine($"App: {app}\nResult: {result}\nTimeout: 90 seconds");
        using var launcher = Start("open", "-n", "-W", app, "--args", result, id, arch);
        var stdout = launcher.StandardOutput.ReadToEndAsync();
        var stderr = launcher.StandardError.ReadToEndAsync();
        var succeeded = false;
        try
        {
            await Wait(launcher, result, id, arch, TimeSpan.FromSeconds(90));
            succeeded = true;
        }
        finally
        {
            // Dedicated CI runner: stop only this smoke executable on failure.
            if (!succeeded) await RunCleanup();
            if (!launcher.HasExited) launcher.Kill(entireProcessTree: true);
            await launcher.WaitForExitAsync();
            var log = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(output, "launch.log"), log);
            if (log.Length > 0) Console.WriteLine("LaunchServices output:\n" + log);
        }
    }

    static async Task RunCleanup()
    {
        try { await Program.Run("pkill", "-x", "CatalystSmoke"); }
        catch (Exception error) { Console.Error.WriteLine($"Smoke cleanup: {error.Message}"); }
    }

    // Exercise the same process/result protocol on Windows without Apple workloads.
    internal static async Task SelfTest()
    {
        var output = Path.Combine(Path.GetTempPath(), "catalyst-tools-" + Guid.NewGuid());
        Directory.CreateDirectory(output);
        foreach (var scenario in new[] { "arm64", "x64", "wrong-id", "wrong-arch", "failed", "invalid", "missing", "nonzero", "timeout" })
        {
            var file = Path.Combine(output, scenario + ".json");
            var id = Guid.NewGuid().ToString();
            using var child = Start("dotnet", Assembly.GetExecutingAssembly().Location, "self-test-child", scenario, file, id);
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            var passed = false;
            try
            {
                await Wait(child, file, id, scenario == "x64" ? "x64" : "arm64", TimeSpan.FromSeconds(scenario == "timeout" ? 1 : 10));
                passed = true;
            }
            catch (Exception error) when (error is InvalidOperationException or JsonException or TimeoutException) { }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                await stdout;
                await stderr;
            }
            Require(passed == (scenario is "arm64" or "x64"), $"Unexpected launcher self-test result: {scenario}");
            Console.WriteLine($"{scenario}: passed");
            if (File.Exists(file)) File.Delete(file);
        }
        Directory.Delete(output); // Only the empty directory created by this test.
    }

    internal static async Task<int> TestChild(string scenario, string file, string id)
    {
        if (scenario == "timeout") { await Task.Delay(TimeSpan.FromMinutes(1)); return 0; }
        if (scenario == "missing") return 0;
        var json = scenario == "invalid" ? "invalid JSON" : JsonSerializer.Serialize(new
        {
            RunId = scenario == "wrong-id" ? "stale" : id,
            Success = scenario != "failed",
            Architecture = scenario is "x64" or "wrong-arch" ? "X64" : "Arm64"
        });
        await File.WriteAllTextAsync(file + ".tmp", json);
        File.Move(file + ".tmp", file);
        return scenario == "nonzero" ? 1 : 0;
    }
}
