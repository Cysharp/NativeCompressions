using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Metrology;
using System.Reflection;

namespace NativeCompressions.BenchmarkHelper;

internal record PayloadData(byte[] Source, byte[] Compressed);

public class PayloadSizeColumnAttribute : ColumnConfigBaseAttribute
{
    public PayloadSizeColumnAttribute()
        : base(new PayloadSizeColumn())
    {
    }
}

public class CompressionRatioColumnAttribute : ColumnConfigBaseAttribute
{
    public CompressionRatioColumnAttribute()
        : base(new CompressionRatioColumn())
    {
    }
}

public class CompressionThroughputColumnAttribute : ColumnConfigBaseAttribute
{
    public CompressionThroughputColumnAttribute()
        : base(new CompressionThroughputColumn())
    {
    }
}

public class PayloadSizeColumn : IColumn
{
    public string Id => nameof(PayloadSizeColumn);

    public string ColumnName => "Payload";

    public bool AlwaysShow => true;

    public ColumnCategory Category => ColumnCategory.Custom;

    public int PriorityInCategory => 0;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Size;

    public string Legend => "Payload size";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        var methodInfo = benchmarkCase.Descriptor.Type.GetMethod("GetPayloadData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (methodInfo == null) return "-";

        var instance = Activator.CreateInstance(benchmarkCase.Descriptor.Type);
        var parameterLevel = benchmarkCase.Parameters[0].Value;

        var payloadData = (PayloadData)methodInfo.Invoke(instance, [parameterLevel])!;
        if (benchmarkCase.Descriptor.HasCategory("Compress"))
        {
            return new SizeValue(payloadData.Compressed.Length).ToString();
        }
        else if (benchmarkCase.Descriptor.HasCategory("Decompress"))
        {
            return new SizeValue(payloadData.Source.Length).ToString();
        }

        return "-";
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        return GetValue(summary, benchmarkCase);
    }

    public bool IsAvailable(Summary summary)
    {
        return true;
    }

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase)
    {
        return false;
    }
}

public class CompressionRatioColumn : IColumn
{
    public string Id => nameof(CompressionRatioColumn);

    public string ColumnName => "Ratio";

    public bool AlwaysShow => true;

    public ColumnCategory Category => ColumnCategory.Custom;

    public int PriorityInCategory => 0;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Size;

    public string Legend => "Compression Ratio";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        if (benchmarkCase.Descriptor.HasCategory("Compress"))
        {
            var methodInfo = benchmarkCase.Descriptor.Type.GetMethod("GetPayloadData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (methodInfo == null) return "-";

            var instance = Activator.CreateInstance(benchmarkCase.Descriptor.Type);
            var parameterLevel = benchmarkCase.Parameters[0].Value;

            var payloadData = (PayloadData)methodInfo.Invoke(instance, [parameterLevel])!;

            var ratio = (double)payloadData.Source.Length / payloadData.Compressed.Length;

            return ratio.ToString("0.00");
        }

        return "-";
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        return GetValue(summary, benchmarkCase);
    }

    public bool IsAvailable(Summary summary)
    {
        return true;
    }

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase)
    {
        return false;
    }
}

public class CompressionThroughputColumn : IColumn
{
    public string Id => nameof(CompressionThroughputColumn);

    public string ColumnName => "Throughput";

    public bool AlwaysShow => true;

    public ColumnCategory Category => ColumnCategory.Custom;

    public int PriorityInCategory => 0;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Time;

    public string Legend => "Compression Throughput";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        var mean = summary.Reports.FirstOrDefault(x => x.BenchmarkCase == benchmarkCase)?.ResultStatistics?.Mean;
        if (mean == null) return "-";

        var methodInfo = benchmarkCase.Descriptor.Type.GetMethod("GetPayloadData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (methodInfo == null) return "-";

        var instance = Activator.CreateInstance(benchmarkCase.Descriptor.Type);
        var parameterLevel = benchmarkCase.Parameters[0].Value;

        var payloadData = (PayloadData)methodInfo.Invoke(instance, [parameterLevel])!;
        var dataSize = payloadData.Source.Length;

        var seconds = mean.Value / 1_000_000_000.0; // nanosecs to seconds

        var throughput = dataSize / seconds; // bytes per second
        return new SizeValue((long)throughput).ToString() + "/s";
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        return GetValue(summary, benchmarkCase);
    }

    public bool IsAvailable(Summary summary)
    {
        return true;
    }

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase)
    {
        return false;
    }
}
