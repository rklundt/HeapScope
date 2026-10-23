using System.Runtime;
using HeapScope.Contracts;
using HeapScope.Worker;
using HeapScope.Worker.Rpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

const int UsageError = 2;

// The test hook switch takes no value, so it is removed before the key/value command line provider sees it.
var enableTestHooks = args.Contains(WorkerOptions.EnableTestHooksSwitch, StringComparer.Ordinal);
var configArgs = args.Where(a => !string.Equals(a, WorkerOptions.EnableTestHooksSwitch, StringComparison.Ordinal)).ToArray();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddCommandLine(configArgs, WorkerOptions.CreateSwitchMappings());

// The worker logs single-line records to stderr only; the client forwards each line into its own log
// (ARCHITECTURE.md 4.3 and 11).
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.ColorBehavior = LoggerColorBehavior.Disabled;
    o.TimestampFormat = "HH:mm:ss.fff ";
});
builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.AddDebug();

// Heavy analysis must never starve the UI or the profiled application of CPU (ARCHITECTURE.md 4.1, 8).
using (var self = System.Diagnostics.Process.GetCurrentProcess())
{
    self.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
}

// Options come from the command line only, never from environment variables or settings files: the client decides,
// so a privacy-relevant switch (symbol server downloads, ADR-017) cannot be turned on behind its back.
var commandLine = new ConfigurationBuilder().AddCommandLine(configArgs, WorkerOptions.CreateSwitchMappings()).Build();
var optionsValid = WorkerOptions.TryRead(commandLine, enableTestHooks, out var options, out var optionsError);
if (options is not null)
{
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(new ResultSetStore());
    builder.Services.AddSingleton(new HeapScope.Storage.IndexCacheLocator());
    builder.Services.AddSingleton<HeapScope.Worker.Snapshots.SnapshotStore>();
    builder.Services.AddSingleton<WorkerService>();
    builder.Services.AddHostedService<ParentProcessWatcher>();
    builder.Services.AddHostedService<WorkerRpcServer>();
}

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HeapScope.Worker");

if (!optionsValid || options is null)
{
    logger.LogError("{Error}", optionsError);
    return UsageError;
}

logger.LogInformation(
    "{Product} worker {Version} started: pid {Pid}, pipe {Pipe}, parent {ParentPid}, server GC {ServerGc}, test hooks {TestHooks}",
    ProductInfo.Name,
    ProductInfo.Version,
    Environment.ProcessId,
    options.Pipe,
    options.ParentPid,
    GCSettings.IsServerGC,
    options.EnableTestHooks);

if (options.AllowMicrosoftSymbolServer)
{
    logger.LogWarning("Symbol server downloads are on (Symbols:AllowMicrosoftSymbolServer): a missing DAC may be downloaded from Microsoft's symbol server, which sends module identifiers (never dump contents)");
}

await host.RunAsync().ConfigureAwait(false);
logger.LogInformation("Worker stopped");
return 0;
