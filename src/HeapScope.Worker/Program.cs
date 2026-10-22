using System.Runtime;
using HeapScope.Contracts;
using HeapScope.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const int UsageError = 2;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddCommandLine(args, WorkerOptions.CreateSwitchMappings());

// The worker logs to stderr only; the client forwards it into its own log (ARCHITECTURE.md 11).
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.AddDebug();

var optionsValid = WorkerOptions.TryRead(builder.Configuration, out var options, out var optionsError);
if (options is not null)
{
    builder.Services.AddSingleton(options);
    builder.Services.AddHostedService<ParentProcessWatcher>();
}

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HeapScope.Worker");

if (!optionsValid || options is null)
{
    logger.LogError("{Error}", optionsError);
    return UsageError;
}

logger.LogInformation(
    "{Product} worker {Version} started: pid {Pid}, pipe {Pipe}, parent {ParentPid}, server GC {ServerGc}",
    ProductInfo.Name,
    ProductInfo.Version,
    Environment.ProcessId,
    options.Pipe,
    options.ParentPid,
    GCSettings.IsServerGC);

await host.RunAsync().ConfigureAwait(false);
logger.LogInformation("Worker stopped");
return 0;
