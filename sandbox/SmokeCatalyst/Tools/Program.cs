using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace CatalystSmoke.Tools;

static class Program
{
    internal const string PackageVersion = "0.0.0-catalyst-phase2";

    static async Task<int> Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["verify-packages", var feed, var root]: Packages.Verify(feed, root); break;
                case ["verify-assets", var output, var evidence, var arch]: Packages.VerifyAssets(output, evidence, Arch(arch)); break;
                case ["verify-targets", var feed, var output, var arch]: await Targets.Verify(feed, output, Arch(arch)); break;
                case ["run-app", var app, var evidence, var arch]: await Launcher.Run(app, evidence, Arch(arch)); break;
                case ["self-test"]: await Launcher.SelfTest(); break;
                case ["self-test-child", var scenario, var result, var id]: return await Launcher.TestChild(scenario, result, id);
                default: throw new ArgumentException("Commands: verify-packages <feed> <repo-root> | verify-assets <output> <evidence> <arch> | verify-targets <feed> <output> <arch> | run-app <app> <evidence> <arch> | self-test");
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    internal static string Arch(string arch) => arch is "arm64" or "x64" ? arch : throw new ArgumentException($"Invalid architecture: {arch}");
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    internal static JsonNode ReadJson(string file) => JsonNode.Parse(File.ReadAllText(file)) ?? throw new InvalidDataException(file);
    internal static IEnumerable<string> Find(string directory, string pattern) => Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories);
    internal static Process Start(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {file}");
    }
    internal static async Task<(int Code, string Output)> Run(string file, params string[] args)
    {
        using var process = Start(file, args);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }
    internal static async Task<string> Dotnet(params string[] args)
    {
        var result = await Run("dotnet", args);
        Require(result.Code == 0, result.Output);
        return result.Output;
    }
    internal static XElement Properties(Dictionary<string, string> properties) => new("PropertyGroup", properties.Select(p => new XElement(p.Key, p.Value)));
}
