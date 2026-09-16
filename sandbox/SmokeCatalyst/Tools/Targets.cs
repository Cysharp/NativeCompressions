using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using static CatalystSmoke.Tools.Program;

namespace CatalystSmoke.Tools;

static class Targets
{
    internal static async Task Verify(string feed, string output, string arch)
    {
        foreach (var library in new[] { "LZ4", "Zstandard", "OpenZL" })
            await VerifyLibrary(feed, Path.Combine(output, library), arch, library);
    }

    static async Task VerifyLibrary(string feed, string output, string arch, string library)
    {
        var archive = library switch { "LZ4" => "liblz4.a", "Zstandard" => "libzstd.a", _ => "libopenzl.a" };
        var targetName = library == "LZ4" ? "Lz4" : library;
        feed = Path.GetFullPath(feed);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var package = $"NativeCompressions.{library}.Runtime.maccatalyst-{arch}";
        var check = "CheckNativeCompressions" + targetName + "Catalyst" + (arch == "arm64" ? "Arm64" : "X64");
        ZipFile.ExtractToDirectory(Path.Combine(feed, $"{package}.{PackageVersion}.nupkg"), Path.Combine(output, "package"), true);
        var targets = Path.Combine(output, "package", "buildTransitive", package + ".targets");
        var defaults = new Dictionary<string, string> { ["TargetFramework"] = "net10.0-maccatalyst", ["TargetPlatformIdentifier"] = "maccatalyst", ["RuntimeIdentifier"] = $"maccatalyst-{arch}", ["OutputType"] = "Exe" };
        void Probe(string file, Dictionary<string, string> props, params string[] imports) => new XElement("Project", Properties(props), imports.Select(p => new XElement("Import", new XAttribute("Project", p)))).Save(file);
        async Task Count(string project, int expected)
        {
            var json = await Dotnet("msbuild", project, "-nologo", "-v:q", "-t:" + check, "-getItem:NativeReference");
            Require(JsonNode.Parse(json)!["Items"]!["NativeReference"]!.AsArray().Count == expected, $"Unexpected NativeReference count: {project}\n{json}");
        }
        var cases = new (string Name, Dictionary<string, string> Overrides, int Count)[]
        {
            ("arm64", new() { ["RuntimeIdentifier"] = "maccatalyst-arm64" }, arch == "arm64" ? 1 : 0),
            ("x64", new() { ["RuntimeIdentifier"] = "maccatalyst-x64" }, arch == "x64" ? 1 : 0),
            ("ios", new() { ["TargetPlatformIdentifier"] = "ios", ["RuntimeIdentifier"] = "ios-arm64" }, 0),
            ("macos", new() { ["TargetPlatformIdentifier"] = "macos", ["RuntimeIdentifier"] = "osx-arm64" }, 0),
            ("windows", new() { ["TargetPlatformIdentifier"] = "", ["RuntimeIdentifier"] = "win-x64" }, 0),
            ("linux", new() { ["TargetPlatformIdentifier"] = "", ["RuntimeIdentifier"] = "linux-x64" }, 0),
            ("outer", new() { ["TargetFramework"] = "", ["IsCrossTargetingBuild"] = "true" }, 0),
            ("universal-outer", new() { ["RuntimeIdentifier"] = "", ["RuntimeIdentifiers"] = "maccatalyst-arm64;maccatalyst-x64" }, 0),
            ("universal-inner", new() { ["RuntimeIdentifiers"] = "maccatalyst-arm64;maccatalyst-x64" }, 1),
            ("library", new() { ["OutputType"] = "Library" }, 0)
        };
        foreach (var item in cases)
        {
            var props = new Dictionary<string, string>(defaults);
            foreach (var pair in item.Overrides) props[pair.Key] = pair.Value;
            var file = Path.Combine(output, item.Name + ".proj");
            Probe(file, props, targets);
            await Count(file, item.Count);
            Console.WriteLine($"{item.Name}: {item.Count} NativeReference");
        }
        var missing = Path.Combine(output, "missing", "buildTransitive");
        Directory.CreateDirectory(missing);
        var missingTargets = Path.Combine(missing, Path.GetFileName(targets));
        File.Copy(targets, missingTargets, true);
        var missingProject = Path.Combine(output, "missing.proj");
        Probe(missingProject, defaults, missingTargets);
        var result = await Run("dotnet", "msbuild", missingProject, "-nologo", "-t:" + check);
        Require(result.Code != 0 && result.Output.Contains("is missing its Catalyst archive"), result.Output);
        Console.WriteLine("Missing archive: expected explicit failure");

        async Task<string> Restore(string folder, string[] ids, string? rid = null)
        {
            Directory.CreateDirectory(folder);
            var project = Path.Combine(folder, "Consumer.csproj");
            new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                Properties(new() { ["TargetFramework"] = "netstandard2.1", ["EnableDefaultItems"] = "false" }),
                new XElement("ItemGroup", ids.Select(id => new XElement("PackageReference", new XAttribute("Include", id), new XAttribute("Version", PackageVersion))))).Save(project);
            var args = new List<string> { "restore", project, "--source", feed, "--packages", Path.Combine(folder, "cache"), "-p:NuGetAudit=false", "-p:ArtifactsPath=" + Path.Combine(folder, "artifacts") };
            if (rid != null) args.AddRange(["-r", rid]);
            await Dotnet(args.ToArray());
            return Path.Combine(folder, "artifacts");
        }
        foreach (var (name, ids) in new (string, string[])[] { ("direct", [package]), ("aggregate", [$"NativeCompressions.{library}.Runtime"]), ("duplicate", [package, $"NativeCompressions.{library}.Runtime"]) })
        {
            var folder = Path.Combine(output, "restore-" + name);
            var artifacts = await Restore(folder, ids);
            var imports = Find(artifacts, "*.nuget.g.targets").Single();
            Require(XDocument.Load(imports).Descendants().Count(e => e.Name.LocalName == "Import" && ((string?)e.Attribute("Project"))?.EndsWith(package + ".targets") == true) == 1, "Missing or repeated transitive import");
            var probe = Path.Combine(folder, "Evaluate.proj");
            Probe(probe, new(defaults) { ["TargetFramework"] = "netstandard2.1" }, Path.ChangeExtension(imports, ".props"), imports);
            await Count(probe, library == "OpenZL" ? 3 : 1);
            Console.WriteLine($"NuGet {library} {name}: expected transitive NativeReferences");
        }
        foreach (var rid in new[] { "maccatalyst-arm64", "maccatalyst-x64", "ios-arm64", "ios-x64" })
        {
            var artifacts = await Restore(Path.Combine(output, "rid-" + rid), [$"NativeCompressions.{library}.Runtime"], rid);
            var target = Packages.Target(ReadJson(Find(artifacts, "project.assets.json").Single()), rid);
            var native = Packages.NativeAssets(target);
            var expected = library == "OpenZL" && rid.StartsWith("maccatalyst-")
                ? new[] { $"runtimes/{rid}/native/libopenzl.a", $"runtimes/{rid}/native/liblz4.a", $"runtimes/{rid}/native/libzstd.a" }
                : new[] { $"runtimes/{rid}/native/{archive}" };
            Require(native.Order().SequenceEqual(expected.Order()), $"Incorrect {rid} assets: {string.Join(", ", native)}");
            Console.WriteLine($"{rid}: {string.Join(", ", native)}");
        }
    }
}
