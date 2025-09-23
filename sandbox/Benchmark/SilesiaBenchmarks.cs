using NativeCompressions.BenchmarkHelper;
using System.IO.Compression;

namespace Benchmark;

public class Silesia_Lz4 : Lz4BenchmarkBase
{
    protected override byte[] GetTargetSource()
    {
        return Resources.Silesia;
    }
}

public class Silesia_Zstandard : ZstandardBenchmarkBase
{
    protected override byte[] GetTargetSource()
    {
        return Resources.Silesia;
    }
}

public class Silesia_Brotli : BrotliBenchmarkBase
{
    protected override byte[] GetTargetSource()
    {
        return Resources.Silesia;
    }
}

public class Silesia_GZip : GZipBenchmarkBase
{
    protected override byte[] GetTargetSource()
    {
        return Resources.Silesia;
    }
}
