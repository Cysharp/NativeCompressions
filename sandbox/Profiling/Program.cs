using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NativeCompressions.LZ4;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;

// Use SigNoz profiling: (view: http://localhost:8080/ )
// git clone https://github.com/SigNoz/signoz.git
// cd signoz/deploy/docker
// docker compose up

// args = ["--max-degree-of-parallelism", "4"];

var endPoint = new Uri("http://localhost:4317"); // OpenTelemetry URI(we use SigNoz)
var rootActivitySource = new ActivitySource("ProfilingApp");

var builder = Host.CreateApplicationBuilder();

builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
});

// AddOpenTelemetryExporters
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource =>
    {
        resource.AddService("NativeCompressions Profiling App");
    })
    .WithMetrics(metrics =>
    {
        metrics.AddRuntimeInstrumentation()
            .AddOtlpExporter(options => options.Endpoint = endPoint);
    })
    .WithTracing(tracing =>
    {
        tracing.SetSampler(new AlwaysOnSampler())
            .AddSource("NativeCompressions.LZ4")
            .AddSource("ProfilingApp")
            .AddOtlpExporter(options => options.Endpoint = endPoint);
        // .AddConsoleExporter(); // debug
    })
    .WithLogging(logging =>
    {
        logging.AddOtlpExporter(options => options.Endpoint = endPoint);
    });

// Use ConsoleAppFramework
var app = builder.ToConsoleAppBuilder();

// prepare LZ4 data
var linkedCompressed = File.ReadAllBytes("silesia.tar.lz4");
var original = LZ4.Decompress(linkedCompressed);
var blockIndependenCompressed = LZ4.Compress(original);

app.Add("", async ([FromServices] IServiceProvider serviceProvider, [FromServices] ILogger<Program> logger, int? maxDegreeOfParallelism = null) =>
{
    using var _ = rootActivitySource.StartActivity("Multithreading LZ4 Compress");

    var writer = new ArrayBufferPipeWriter();
    await LZ4.CompressAsync(original, writer, maxDegreeOfParallelism: maxDegreeOfParallelism);
    var count = writer.WrittenCount;
    logger.LogInformation("Multithreading Compress Count:" + count + "B");
});

// Quic hack to start opentelemetly host initialize...
var services = ConsoleApp.ServiceProvider!.GetRequiredService<IEnumerable<IHostedService>>();
foreach (var item in services) await item.StartAsync(CancellationToken.None);

await app.RunAsync(args);

foreach (var item in services) await item.StopAsync(CancellationToken.None);
