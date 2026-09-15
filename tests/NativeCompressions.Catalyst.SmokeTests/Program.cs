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
            if (args.Length != 2) throw new ArgumentException("Expected result path and run ID.");
            var resultPath = args[0];
            var result = new SmokeResult { RunId = args[1], Architecture = RuntimeInformation.ProcessArchitecture.ToString() };
            try
            {
                if (!OperatingSystem.IsMacCatalyst() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
                    throw new InvalidOperationException("Phase 1 requires a native ARM64 Catalyst process.");
                result.Version = LZ4.Version;
                if (LZ4.VersionNumber <= 0) throw new InvalidOperationException("Invalid LZ4 version.");
                var source = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray();
                var compressed = LZ4.Compress(source);
                var restored = new byte[source.Length];
                var length = LZ4.Decompress(compressed, restored);
                if (length != source.Length || !source.AsSpan().SequenceEqual(restored))
                    throw new InvalidOperationException("LZ4 round trip did not reproduce the original bytes.");
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
    public bool Success { get; set; }
    public string? Error { get; set; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SmokeResult))]
internal partial class SmokeJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
