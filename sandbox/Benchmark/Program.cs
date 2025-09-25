using Benchmark;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using NativeCompressions;
using System.IO.Compression;
using System.Reflection;


var config = ManualConfig.CreateMinimumViable()
    .AddDiagnoser(MemoryDiagnoser.Default)
    // .AddColumn(StatisticColumn.OperationsPerSecond)
    //.AddExporter(DefaultExporters.Plain)
    .AddExporter(MarkdownExporter.Default)
    .AddJob(Job.Default.WithWarmupCount(1).WithIterationCount(1)); // .AddJob(Job.ShortRun);




BenchmarkRunner.Run<Silesia_GZip>(config, args);
// BenchmarkSwitcher.FromAssembly(Assembly.GetEntryAssembly()!).Run(args, config);
