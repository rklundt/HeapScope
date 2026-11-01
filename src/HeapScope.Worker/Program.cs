// HeapScope
// Copyright (C) 2026 Ray Klundt
// https://github.com/rklundt/HeapScope
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, version 3 only, with additional terms
// under Section 7 of that license.
//
// Additional terms, including attribution requirements, apply.
// See the ADDITIONAL-TERMS.md file distributed with this program.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program (see the LICENSE file). If not, see
// <https://www.gnu.org/licenses/>.
//
// Commercial licenses are available. See COMMERCIAL.md.

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

// Options come from the command line only, never from environment variables or settings files: the client decides,
// so a privacy-relevant switch (symbol server downloads, ADR-017) cannot be turned on behind its back.
var commandLine = new ConfigurationBuilder().AddCommandLine(configArgs, WorkerOptions.CreateSwitchMappings()).Build();
var optionsValid = WorkerOptions.TryRead(commandLine, enableTestHooks, out var options, out var optionsError);

// The worker logs single-line records to stderr only; the client forwards each line into its own log
// (ARCHITECTURE.md 4.3 and 11). Scopes carry the client's trace id, which StreamJsonRpc sends with each request, so a
// worker line can be joined to the UI or CLI action that caused it (sprint 2.3.7 review M1).
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.IncludeScopes = true;
    o.ColorBehavior = LoggerColorBehavior.Disabled;
    o.TimestampFormat = "HH:mm:ss.fff ";
});
builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.Configure(o => o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId);
builder.Logging.SetMinimumLevel(options?.LogLevel ?? LogLevel.Information);
builder.Logging.AddDebug();

// Heavy analysis must never starve the UI or the profiled application of CPU (ARCHITECTURE.md 4.1, 8).
using (var self = System.Diagnostics.Process.GetCurrentProcess())
{
    self.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
}

if (options is not null)
{
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(new ResultSetStore());
    builder.Services.AddSingleton(new HeapScope.Storage.IndexCacheLocator(options.Folders.Cache));

    // Index caches are signed with this user's key and a cache without its signature is never used (ADR-036): a cache
    // shipped next to a dump cannot decide what the analysis shows. Injected, so no code path reads caches unchecked.
    builder.Services.AddSingleton(sp => LoadSigner(options, sp.GetRequiredService<ILoggerFactory>().CreateLogger("HeapScope.Worker")));
    builder.Services.AddSingleton<HeapScope.Worker.Snapshots.SnapshotStore>();
    builder.Services.AddSingleton<HeapScope.Worker.Traces.TraceStore>();
    builder.Services.AddWorkerRpcTargets();
    builder.Services.AddHostedService<ParentProcessWatcher>();
    builder.Services.AddHostedService<WorkerRpcServer>();
}

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HeapScope.Worker");

// A crash leaves one structured line before the process dies, with the memory it had reached (sprint 2.3.7 review M19):
// the client sees only the exit code.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    using var self = System.Diagnostics.Process.GetCurrentProcess();
    logger.LogCritical(
        e.ExceptionObject as Exception,
        "Worker terminating on an unhandled exception: peak working set {PeakMb:N0} MB, GC heap {HeapMb:N0} MB",
        self.PeakWorkingSet64 / (1024 * 1024),
        GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024));
};

if (!optionsValid || options is null)
{
    logger.LogError("{Error}", optionsError);
    return UsageError;
}

// The GC's own view of its settings (sprint 2.3.6 review): whether dynamic adaptation (DATAS) is on next to the heap count
// the client sets (ARCHITECTURE.md 9). The build, system, architecture and runtime: an architecture mismatch or a
// self-contained worker's own DAC depend on them (sprint 2.3.7 review H4).
var gc = GC.GetConfigurationVariables();
logger.LogInformation(
    "{Product} worker {Version} started: pid {Pid}, pipe {Pipe}, parent {ParentPid}, protocol {Protocol}; {Environment}; path {Path}; server GC {ServerGc}, GC heaps {GcHeaps}, GC dynamic adaptation {Datas}; DAC folder {DacFolder}; data folder {DataFolder} ({DataFolderSource}); log level {LogLevel}; test hooks {TestHooks}",
    ProductInfo.Name,
    ProductInfo.InformationalVersion,
    Environment.ProcessId,
    options.Pipe,
    options.ParentPid,
    WorkerProtocol.Version,
    RuntimeEnvironment.Summary,
    Environment.ProcessPath,
    GCSettings.IsServerGC,
    gc.GetValueOrDefault("HeapCount"),
    gc.GetValueOrDefault("GCDynamicAdaptationMode"),
    options.DacFolder ?? "(none)",
    options.Folders.Root,
    options.Folders.Source,
    options.LogLevel,
    options.EnableTestHooks);

// The versions of every rule and format a result depends on (sprint 2.3.7 review M5): a result disputed after an
// upgrade is traced to the rules that produced it.
logger.LogInformation("Worker versions: {Versions}", WorkerVersions.Describe());

// The index cache signer is loaded now, once the log exists, so its outcome and a failure are on record (3.7 review H4).
host.Services.GetRequiredService<HeapScope.Storage.IndexCacheSigner>();

if (options.AllowMicrosoftSymbolServer)
{
    logger.LogWarning("Symbol server downloads are on (Symbols:AllowMicrosoftSymbolServer): a missing DAC may be downloaded from Microsoft's symbol server, which sends module identifiers (never dump contents)");
}

await host.RunAsync().ConfigureAwait(false);
logger.LogInformation("Worker stopped");
return 0;

static HeapScope.Storage.IndexCacheSigner LoadSigner(WorkerOptions options, ILogger logger)
{
    var (signer, key) = HeapScope.Storage.IndexCacheSigner.ForThisUser(options.Folders.Root);
    if (key.Error is { } error)
    {
        logger.LogWarning(
            "Index cache key {Path} could not be read or created ({Error}: {Message}); this run signs with a key of its own, so index caches written now are rebuilt on the next run. Check that the folder exists, belongs to this user and has free space.",
            key.Path,
            error.GetType().Name,
            error.Message);
    }
    else
    {
        logger.LogInformation("Index cache key {Path}: {Source}", key.Path, key.Source);
    }

    return signer;
}
