using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using static CatalystSmoke.Tools.Program;

namespace CatalystSmoke.Tools;

static class Packages
{
    internal static void Verify(string feed, string root)
    {
        ZipArchive Open(string name) => ZipFile.OpenRead(Path.Combine(feed, $"{name}.{PackageVersion}.nupkg"));
        HashSet<string> Dependencies(string name)
        {
            using var zip = Open(name);
            using var stream = zip.GetEntry($"{name}.nuspec")!.Open();
            var groups = XDocument.Load(stream).Descendants().Where(e => e.Name.LocalName == "group").ToArray();
            Require(groups.Length > 0, $"No dependency groups: {name}");
            return groups.SelectMany(g => g.Elements()).Select(e => (string)e.Attribute("id")!).ToHashSet();
        }
        foreach (var arch in new[] { "arm64", "x64" })
        {
            var runtime = $"NativeCompressions.LZ4.Runtime.maccatalyst-{arch}";
            using var zip = Open(runtime);
            var native = $"runtimes/maccatalyst-{arch}/native/liblz4.a";
            Require(zip.GetEntry($"buildTransitive/{runtime}.targets") != null, $"Missing targets: {runtime}");
            Require(zip.Entries.Count(e => e.FullName.EndsWith(".a")) == 1, $"Expected one archive: {runtime}");
            using var data = new MemoryStream();
            using (var input = zip.GetEntry(native)!.Open()) input.CopyTo(data);
            var bytes = data.ToArray();
            Require(bytes.AsSpan().StartsWith("!<arch>\n"u8), $"Invalid archive: {native}");
            Require(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "src", "NativeCompressions.LZ4.Runtime", native))), $"Archive differs from checkout: {native}");
            Require(Dependencies("NativeCompressions.LZ4.Runtime").Contains(runtime), $"Missing dependency: {runtime}");
        }
        using (var zip = Open("NativeCompressions.LZ4.Core"))
        {
            Require(zip.GetEntry("lib/netstandard2.1/NativeCompressions.LZ4.Core.dll") != null, "Missing generic Core DLL");
            Require(zip.Entries.Any(e => e.FullName.StartsWith("lib/net10.0-maccatalyst") && e.FullName.EndsWith("NativeCompressions.LZ4.Core.dll")), "Missing Catalyst Core DLL");
        }
        Require(Dependencies("NativeCompressions.LZ4").IsSupersetOf(["NativeCompressions.LZ4.Core", "NativeCompressions.LZ4.Runtime"]), "Missing meta dependencies");
        Require(Dependencies("CatalystSmoke.Middle").Contains("NativeCompressions.LZ4"), "Missing middle dependency");
        Console.WriteLine("Local NuGet files and dependency groups verified.");
    }

    internal static JsonObject Target(JsonNode assets, string rid) => assets["targets"]!.AsObject().Single(p => p.Key.EndsWith('/' + rid)).Value!.AsObject();
    internal static string[] NativeAssets(JsonObject target) => target.SelectMany(p => p.Value?["native"]?.AsObject().Select(n => n.Key) ?? []).Where(p => p.EndsWith(".a") || p.EndsWith(".dylib")).ToArray();
    internal static void VerifyAssets(string output, string evidence, string arch)
    {
        Directory.CreateDirectory(evidence);
        var rid = $"maccatalyst-{arch}";
        var file = Find(output, "project.assets.json").Single();
        File.Copy(file, Path.Combine(evidence, "project.assets.json"), true);
        var target = Target(ReadJson(file), rid);
        var core = target[$"NativeCompressions.LZ4.Core/{PackageVersion}"]!;
        foreach (var kind in new[] { "compile", "runtime" })
        {
            var dlls = core[kind]!.AsObject().Select(p => p.Key).Where(p => p.EndsWith(".dll")).ToArray();
            Require(dlls.Length == 1 && dlls[0].StartsWith("lib/net10.0-maccatalyst"), $"Incorrect {kind} Core DLL: {string.Join(", ", dlls)}");
        }
        var native = NativeAssets(target);
        Require(native.SequenceEqual([$"runtimes/{rid}/native/liblz4.a"]), $"Incorrect native assets: {string.Join(", ", native)}");
        File.WriteAllText(Path.Combine(evidence, "native-asset-candidates.json"), JsonSerializer.Serialize(native));
        var references = Find(output, "native-references.txt").Single();
        var fields = File.ReadAllLines(references).Single().Split('|');
        Require(fields.Length == 2 && fields[1] == "Static" && fields[0].Replace('\\', '/').ToLowerInvariant().Contains($"/nativecompressions.lz4.runtime.{rid}/") && File.Exists(fields[0]), "Expected one static archive from the Catalyst NuGet package");
        File.Copy(references, Path.Combine(evidence, "native-references.txt"), true);
        Console.WriteLine("Catalyst Core DLL and native asset selection verified.");
    }
}
