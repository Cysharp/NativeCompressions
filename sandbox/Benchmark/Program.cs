using Benchmark;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using System.IO.Compression;
using System.Reflection;

#if !DEBUG

var config = ManualConfig.CreateMinimumViable()
    .AddDiagnoser(MemoryDiagnoser.Default)
    // .AddColumn(StatisticColumn.OperationsPerSecond)
    //.AddExporter(DefaultExporters.Plain)
    .AddExporter(MarkdownExporter.Default)
    .AddJob(Job.Default.WithWarmupCount(1).WithIterationCount(1)); // .AddJob(Job.ShortRun);

BenchmarkSwitcher.FromAssembly(Assembly.GetEntryAssembly()!).Run(args, config);


//BenchmarkDotNet.Running.BenchmarkRunner.Run<Lz4Simple2>(config, args);

#else

global::System.Console.WriteLine("DEBUG BUILD.");

var bench = new ZstandardSimpleEncode();
// var i = bench.NativeCompressions_Zstandard_Compress_Multithread();
var i2 = bench.GZipStream_Optimal();
//Console.WriteLine(i);
Console.WriteLine(i2);

return;

//var lz4 = new Lz4Simple();
//lz4.Init();

//Console.WriteLine(LZ4.LZ4Codec.CodecName);

//var k4 = lz4.K4os_Lz4_Encode();
//var k4Result = lz4.buffer.AsSpan(0, k4).ToArray();

//lz4.Init();
//var my = lz4.NativeCompressions_Lz4_Encode();
//var myResult = lz4.buffer.AsSpan(0, my).ToArray();


//Console.WriteLine(myResult.AsSpan().SequenceEqual(k4Result));

#endif
