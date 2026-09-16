using System.Runtime.InteropServices;
using System.Text.Json;
using Foundation;
using NativeCompressions;
using UIKit;

namespace CatalystSmoke;

public static class Program
{
    internal static string[] Arguments = [];

    public static void Main(string[] args)
    {
        Arguments = args;
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        BeginInvokeOnMainThread(Run);
        return true;
    }

    static void Run()
    {
        var exitCode = 1;
        try
        {
            var args = Program.Arguments;
            if (args.Length != 3) throw new ArgumentException("Expected result path, run ID and architecture.");
            var resultPath = args[0];
            var result = new SmokeResult { RunId = args[1], Architecture = RuntimeInformation.ProcessArchitecture.ToString() };
            try
            {
                var expectedArchitecture = args[2] switch
                {
                    "arm64" => Architecture.Arm64,
                    "x64" => Architecture.X64,
                    _ => throw new ArgumentException("Unsupported expected architecture.")
                };
                if (!OperatingSystem.IsMacCatalyst() || RuntimeInformation.ProcessArchitecture != expectedArchitecture)
                    throw new InvalidOperationException($"Expected a {expectedArchitecture} Catalyst process.");
                result.Version = LZ4.Version;
                if (LZ4.VersionNumber <= 0) throw new InvalidOperationException("Invalid LZ4 version.");
#if SMOKE_MIDDLE
                if (Middle.Compression.VersionNumber != LZ4.VersionNumber)
                    throw new InvalidOperationException("Middle library LZ4 version mismatch.");
#endif
                var source = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray();
                var compressed = LZ4.Compress(source);
                var restored = new byte[source.Length];
                var length = LZ4.Decompress(compressed, restored);
                if (length != source.Length || !source.AsSpan().SequenceEqual(restored))
                    throw new InvalidOperationException("LZ4 round trip did not reproduce the original bytes.");
                result.ZstandardVersion = Zstandard.Version;
                if (Zstandard.VersionNumber == 0) throw new InvalidOperationException("Invalid Zstandard version.");
#if SMOKE_MIDDLE
                if (Middle.Compression.ZstandardVersionNumber != Zstandard.VersionNumber)
                    throw new InvalidOperationException("Middle library Zstandard version mismatch.");
#endif
                var zstdSource = Enumerable.Range(0, 8 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
                var options = new ZstandardCompressionOptions { NbWorkers = 2, JobSize = 1024 * 1024 };
                var zstdCompressed = Zstandard.Compress(zstdSource, options);
                var zstdRestored = new byte[zstdSource.Length];
                var zstdLength = Zstandard.Decompress(zstdCompressed, zstdRestored);
                if (zstdLength != zstdSource.Length || !zstdSource.AsSpan().SequenceEqual(zstdRestored))
                    throw new InvalidOperationException("Zstandard multithread round trip did not reproduce the original bytes.");
                result.OpenZLEncodingVersion = OpenZLSmoke.Run();
#if SMOKE_MIDDLE
                if (Middle.Compression.OpenZLEncodingVersion != result.OpenZLEncodingVersion)
                    throw new InvalidOperationException("Middle library OpenZL encoding version mismatch.");
#endif
                result.Success = true;
                exitCode = 0;
            }
            catch (Exception error)
            {
                result.Error = error.ToString();
            }
            // Publish the result atomically; the runner rejects stale IDs and missing results.
            File.WriteAllText(resultPath + ".tmp", JsonSerializer.Serialize(result, SmokeJsonContext.Default.SmokeResult));
            File.Move(resultPath + ".tmp", resultPath, overwrite: true);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
        }
        Environment.Exit(exitCode);
    }
}

internal sealed class SmokeResult
{
    public string RunId { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string? Version { get; set; }
    public string? ZstandardVersion { get; set; }
    public uint OpenZLEncodingVersion { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SmokeResult))]
internal partial class SmokeJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
