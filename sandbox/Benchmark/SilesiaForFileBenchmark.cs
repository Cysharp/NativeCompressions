using NativeCompressions.BenchmarkHelper;

namespace Benchmark;

public class SilesiaForFile_Lz4 : Lz4BenchmarkForFileBase
{
    // TODO: change maxDegreeOfParallelism

    public override IEnumerable<int> GetLevels()
    {
        return [NativeCompressions.LZ4.MinCompressionLevel]; // TODO:remove this.
    }

    public override async ValueTask SetupCoreAsync()
    {
        var data = Resources.Silesia;
        if (!Directory.Exists("lz4_temp"))
        {
            Directory.CreateDirectory("lz4_temp");
        }
        await File.WriteAllBytesAsync("lz4_temp/silesia.lz4", data);
    }

    public override ValueTask CleanupCoreAsync()
    {
        Directory.Delete("lz4_temp", true);
        return default;
    }

    protected override string GetSourceFilePath()
    {
        return "lz4_temp/silesia.lz4";
    }

    protected override string GetCompressDestinationFilePath()
    {
        return "lz4_temp/silesia2.lz4";
    }

    protected override string GetDecompressDestinationFilePath()
    {
        return "lz4_temp/silesia3.lz4";
    }
}

