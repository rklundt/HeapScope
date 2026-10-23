using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using HeapScope.Application;
using HeapScope.Application.Jobs;
using HeapScope.Application.Logging;
using HeapScope.Application.Sessions;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HeapScope.Cli;

/// <summary>
/// Builds the command tree (ADR-008). Exit codes: 0 success, 1 failure, 2 usage error (System.CommandLine),
/// 130 canceled.
/// </summary>
internal static class CliCommands
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Canceled = 130;

    /// <summary>Attach to an already running worker (debugging) instead of launching one.</summary>
    public static readonly Option<string?> PipeOption = new("--pipe")
    {
        Description = "Attach to a worker already listening on this pipe instead of starting one.",
        Recursive = true,
    };

    /// <summary>A folder searched for the data access component (ADR-017), like <c>Symbols:DacFolder</c>.</summary>
    public static readonly Option<string?> DacFolderOption = new("--dac-folder")
    {
        Description = "A folder holding mscordaccore.dll of the dumped runtime, when that runtime is not installed here.",
        Recursive = true,
    };

    /// <summary>Opt-in to symbol server downloads (ADR-017), like <c>Symbols:AllowMicrosoftSymbolServer</c>.</summary>
    public static readonly Option<bool> AllowSymbolServerOption = new("--allow-symbol-server")
    {
        Description = "Allow downloading a missing mscordaccore.dll from Microsoft's symbol server (sends module identifiers, never dump contents).",
        Recursive = true,
    };

    private static Func<IServiceProvider>? s_services;

    /// <summary>
    /// Ctrl+C cancels the command's token and then waits up to an hour for the command to return: a canceled capture
    /// must let the runtime finish writing and then delete the dump (sprint 1.1 review H1). The default (2 seconds)
    /// would return 130 and exit with the dump left on disk; null would turn Ctrl+C handling off altogether.
    /// </summary>
    public static InvocationConfiguration CreateInvocationConfiguration() => new() { ProcessTerminationTimeout = TimeSpan.FromHours(1) };

    public static RootCommand Build(Func<IServiceProvider> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        s_services = services;
        var root = new RootCommand($"{ProductInfo.Name} {ProductInfo.Version}: .NET memory profiler command line");
        root.Options.Add(PipeOption);
        root.Options.Add(DacFolderOption);
        root.Options.Add(AllowSymbolServerOption);

        // The built-in --version prints the raw informational version (with the source revision);
        // the product version is ProductInfo.Version everywhere (ARCHITECTURE.md 15).
        foreach (var option in root.Options)
        {
            if (option is VersionOption)
            {
                option.Action = new ProductVersionAction();
            }
        }

        root.Subcommands.Add(BuildWorkerCommand(services));
        root.Subcommands.Add(BuildSyntheticCommand(services));
        root.Subcommands.Add(BuildOpenCommand(services));
        root.Subcommands.Add(BuildListCommand(services));
        root.Subcommands.Add(BuildCaptureCommand(services));
        return root;
    }

    private static Command BuildListCommand(Func<IServiceProvider> services)
    {
        var command = new Command("list", "List running .NET processes that can be captured.");
        command.SetAction((parseResult, ct) => RunAsync(parseResult, async (output, error) =>
        {
            var capture = services().GetRequiredService<ICaptureService>();
            var processes = await capture.ListProcessesAsync(ct).ConfigureAwait(false);
            await output.WriteLineAsync($"{"PID",8}  {"Name",-28} {"Runtime",-18} {"Elevation",-12} {"Working set",12}  Command line").ConfigureAwait(false);
            foreach (var p in processes)
            {
                var elevation = p.Elevation switch
                {
                    ProcessElevation.Elevated => "elevated",
                    ProcessElevation.NotElevated => "no",
                    _ => "unknown",
                };
                var commandLine = p.CommandLine is { Length: > 80 } text ? text[..77] + "..." : p.CommandLine ?? string.Empty;
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{p.ProcessId,8}  {Truncate(p.Name, 28),-28} {p.RuntimeVersion ?? "?",-18} {elevation,-12} {(p.WorkingSetBytes is { } ws ? Mb(ws) : "?"),12}  {commandLine}")).ConfigureAwait(false);
            }

            if (processes.Any(p => p.NeedsAdministrator))
            {
                await error.WriteLineAsync("Processes marked elevated can only be captured when HeapScope runs as administrator.").ConfigureAwait(false);
            }

            return Success;
        }));
        return command;
    }

    private static Command BuildCaptureCommand(Func<IServiceProvider> services)
    {
        var pid = new Option<int>("--pid") { Description = "Process id to capture (see 'list').", Required = true };
        var output = new Option<FileInfo?>("--out") { Description = "Dump file (or existing folder) to write; default: a new file in the session folder." };
        var sessionOption = new Option<string?>("--session") { Description = "Capture into this existing session (folder name or path under the sessions root) instead of a new one." };
        var label = new Option<string?>("--label") { Description = "A label for the snapshot, such as baseline." };
        var open = new Option<bool>("--open") { Description = "Open the dump after capturing it and print its summary." };
        var command = new Command("capture", "Capture a full memory dump of a running .NET process (it is paused while the dump is written). Ctrl+C cancels.");
        command.Options.Add(pid);
        command.Options.Add(output);
        command.Options.Add(sessionOption);
        command.Options.Add(label);
        command.Options.Add(open);
        command.SetAction((parseResult, ct) => RunAsync(parseResult, async (stdout, error) =>
        {
            var capture = services().GetRequiredService<ICaptureService>();
            if (parseResult.GetValue(sessionOption) is { } sessionName && capture.Library.Open(sessionName) is null)
            {
                await error.WriteLineAsync($"Error: {sessionName} is not a HeapScope session under {capture.Library.Root}.").ConfigureAwait(false);
                return Failure;
            }

            var jobs = services().GetRequiredService<JobTracker>();
            var job = capture.StartCapture(parseResult.GetValue(pid), parseResult.GetValue(output)?.FullName, parseResult.GetValue(label));
            using var progressLine = new ConsoleProgress(jobs, job, error);
            using var cancel = ct.Register(job.Cancel);
            CaptureResult result;
            try
            {
                result = await job.Result.ConfigureAwait(false);
            }
            catch (CaptureFailedException ex)
            {
                await error.WriteLineAsync("Error: " + ex.Message).ConfigureAwait(false);
                return Failure;
            }

            progressLine.Complete();
            await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"Captured {result.Process.Name} (pid {result.Process.ProcessId}): {result.Path} ({Mb(result.SizeBytes)})")).ConfigureAwait(false);
            if (result.Session is { } recorded)
            {
                await stdout.WriteLineAsync($"Session: {recorded.Folder}").ConfigureAwait(false);
            }

            if (result.Warning is { } captureWarning)
            {
                await error.WriteLineAsync("Warning: " + captureWarning).ConfigureAwait(false);
            }

            await error.WriteLineAsync("Note: " + result.SecretsNote).ConfigureAwait(false);
            if (!parseResult.GetValue(open))
            {
                return Success;
            }

            var session = services().GetRequiredService<SnapshotSession>();
            if (await session.GetOpenWarningAsync(result.Path, rebuild: false, ct).ConfigureAwait(false) is { } warning)
            {
                await error.WriteLineAsync("Warning: " + warning).ConfigureAwait(false);
            }

            var openJob = session.StartOpen(result.Path);
            using var openProgress = new ConsoleProgress(jobs, openJob, error);
            using var cancelOpen = ct.Register(openJob.Cancel);
            var handle = await openJob.Result.ConfigureAwait(false);
            openProgress.Complete();
            await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"Opened: {handle.Descriptor.ObjectCount:N0} objects ({Mb((long)handle.Descriptor.TotalSize)}), {handle.Descriptor.TypeCount:N0} types, {handle.Descriptor.OpenSeconds:N1} s")).ConfigureAwait(false);
            return Success;
        }));
        return command;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..(length - 3)] + "...";

    private static Command BuildOpenCommand(Func<IServiceProvider> services)
    {
        var dump = new Argument<FileInfo>("dump") { Description = "Path of the full memory dump (.dmp)." };
        var rebuild = new Option<bool>("--rebuild") { Description = "Ignore an existing index cache and load the dump again." };
        var mode = new Option<string>("--mode") { Description = "Memory mode: auto, memory or mapped.", DefaultValueFactory = _ => "auto" };
        mode.AcceptOnlyFromAmong("auto", "memory", "mapped");
        var top = new Option<int>("--top") { Description = "Number of types to list.", DefaultValueFactory = _ => 20 };
        var noOvercommit = new Option<bool>("--no-overcommit") { Description = "Fail instead of warning when the load may not fit in available memory." };

        var command = new Command("open", "Open a full dump: load it (or reuse its index cache) and print a summary. Ctrl+C cancels.");
        command.Arguments.Add(dump);
        command.Options.Add(rebuild);
        command.Options.Add(mode);
        command.Options.Add(top);
        command.Options.Add(noOvercommit);
        command.SetAction((parseResult, ct) => RunAsync(parseResult, async (output, error) =>
        {
            var file = parseResult.GetValue(dump)!;
            if (!file.Exists)
            {
                await error.WriteLineAsync($"Error: the file {file.FullName} does not exist.").ConfigureAwait(false);
                return Failure;
            }

            var session = services().GetRequiredService<SnapshotSession>();
            if (await session.GetOpenWarningAsync(file.FullName, parseResult.GetValue(rebuild), ct).ConfigureAwait(false) is { } warning)
            {
                await error.WriteLineAsync("Warning: " + warning).ConfigureAwait(false);
                if (parseResult.GetValue(noOvercommit))
                {
                    return Failure;
                }
            }

            var jobs = services().GetRequiredService<JobTracker>();
            var memoryMode = parseResult.GetValue(mode) switch
            {
                "memory" => SnapshotMemoryMode.InMemory,
                "mapped" => SnapshotMemoryMode.Mapped,
                _ => SnapshotMemoryMode.Auto,
            };
            var job = session.StartOpen(file.FullName, parseResult.GetValue(rebuild), memoryMode);
            using var progressLine = new ConsoleProgress(jobs, job, error);
            using var cancel = ct.Register(job.Cancel);
            var handle = await job.Result.ConfigureAwait(false);
            progressLine.Complete();

            var d = handle.Descriptor;
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"""
                Snapshot:     {d.Path}
                Process:      {d.ProcessName ?? "?"} (pid {d.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "?"}), .NET {d.RuntimeVersion ?? "?"}, captured {d.CapturedAtUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "?"}
                Objects:      {d.ObjectCount:N0} ({Mb((long)d.TotalSize)}), {d.TypeCount:N0} types, {d.ReferenceCount:N0} references, {d.RootCount:N0} roots
                Opened:       {d.OpenSeconds:N1} s from {(d.FromCache ? "the index cache" : "the dump")}, {(d.Mode == SnapshotMemoryMode.Mapped ? "mapped" : "in memory")}
                Index cache:  {d.CachePath ?? "not written"}
                """)).ConfigureAwait(false);
            foreach (var note in d.Notes)
            {
                await output.WriteLineAsync("Note: " + note).ConfigureAwait(false);
            }

            var summary = await session.GetTypeSummaryAsync(handle, ct).ConfigureAwait(false);
            var operations = services().GetRequiredService<WorkerOperations>();
            var page = await operations.GetRowsAsync(summary.Id, 0, parseResult.GetValue(top), ct).ConfigureAwait(false);
            await output.WriteLineAsync($"Top {page.Rows.Count} of {page.TotalCount} types by shallow size:").ConfigureAwait(false);
            foreach (var row in page.Rows)
            {
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"  {long.Parse(row[1]!, CultureInfo.InvariantCulture),12:N0} {Mb(long.Parse(row[2]!, CultureInfo.InvariantCulture)),12}  {row[0]}")).ConfigureAwait(false);
            }

            await operations.ReleaseResultSetAsync(summary.Id, ct).ConfigureAwait(false);
            return Success;
        }));
        return command;
    }

    private static Command BuildWorkerCommand(Func<IServiceProvider> services)
    {
        var ping = new Command("ping", "Start (or attach to) the analysis worker and measure a round trip.");
        ping.SetAction((parseResult, ct) => RunAsync(parseResult, async (output, error) =>
        {
            var operations = services().GetRequiredService<WorkerOperations>();
            var elapsed = await operations.PingAsync(ct).ConfigureAwait(false);
            var info = operations.Worker.Info!;
            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Worker {info.ProductVersion} (pid {info.ProcessId}) responded in {elapsed.TotalMilliseconds:N1} ms")).ConfigureAwait(false);
            return Success;
        }));

        var info = new Command("info", "Show the analysis worker's version, process and memory.");
        info.SetAction((parseResult, ct) => RunAsync(parseResult, async (output, error) =>
        {
            var operations = services().GetRequiredService<WorkerOperations>();
            var workerInfo = await operations.GetInfoAsync(ct).ConfigureAwait(false);
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"""
                Version:           {workerInfo.ProductVersion}
                Protocol:          {workerInfo.ProtocolVersion}
                Process id:        {workerInfo.ProcessId}
                Server GC:         {workerInfo.ServerGc}
                Working set:       {Mb(workerInfo.WorkingSetBytes)}
                Managed heap:      {Mb(workerInfo.ManagedHeapBytes)}
                Available memory:  {Mb(workerInfo.TotalAvailableMemoryBytes)}
                """)).ConfigureAwait(false);
            return Success;
        }));

        var worker = new Command("worker", "Commands for the analysis worker process.");
        worker.Subcommands.Add(ping);
        worker.Subcommands.Add(info);
        return worker;
    }

    private static Command BuildSyntheticCommand(Func<IServiceProvider> services)
    {
        var objects = new Option<int>("--objects") { Description = "Number of objects.", DefaultValueFactory = _ => 5_000_000 };
        var refs = new Option<double>("--refs") { Description = "Average references per object.", DefaultValueFactory = _ => 3 };
        var seed = new Option<int>("--seed") { Description = "Random seed.", DefaultValueFactory = _ => 42 };
        var top = new Option<int>("--top") { Description = "Number of types to list.", DefaultValueFactory = _ => 10 };

        var command = new Command("synthetic", "Build a synthetic object graph in the worker (diagnostics). Ctrl+C cancels.");
        command.Options.Add(objects);
        command.Options.Add(refs);
        command.Options.Add(seed);
        command.Options.Add(top);
        command.SetAction((parseResult, ct) => RunAsync(parseResult, async (output, error) =>
        {
            var operations = services().GetRequiredService<WorkerOperations>();
            var request = new SyntheticJobRequest(parseResult.GetValue(objects), parseResult.GetValue(refs), parseResult.GetValue(seed));
            var job = operations.StartSyntheticJob(request);
            using var progressLine = new ConsoleProgress(operations.Jobs, job, error);
            using var cancel = ct.Register(job.Cancel);
            var result = await job.Result.ConfigureAwait(false);
            progressLine.Complete();

            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{result.ObjectCount:N0} objects, {result.ReferenceCount:N0} references, {Mb((long)result.TotalSize)} shallow size, {Mb(result.ColumnBytes)} graph columns, {result.ElapsedSeconds:N2} s")).ConfigureAwait(false);
            var page = await operations.GetRowsAsync(result.TypeSummary.Id, 0, parseResult.GetValue(top), ct).ConfigureAwait(false);
            await output.WriteLineAsync($"Top {page.Rows.Count} of {page.TotalCount} types by shallow size:").ConfigureAwait(false);
            foreach (var row in page.Rows)
            {
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"  {row[0],-24} {long.Parse(row[1]!, CultureInfo.InvariantCulture),12:N0} objects {Mb(long.Parse(row[2]!, CultureInfo.InvariantCulture)),12}")).ConfigureAwait(false);
            }

            await operations.ReleaseResultSetAsync(result.TypeSummary.Id, ct).ConfigureAwait(false);
            return Success;
        }));
        return command;
    }

    /// <summary>Runs a command body with the CLI's error handling boundary (ARCHITECTURE.md 11).</summary>
    private static async Task<int> RunAsync(ParseResult parseResult, Func<TextWriter, TextWriter, Task<int>> body)
    {
        var logger = s_services?.Invoke().GetService<ILoggerFactory>()?.CreateLogger("HeapScope.Cli");
        var output = parseResult.InvocationConfiguration.Output;
        var error = parseResult.InvocationConfiguration.Error;
        try
        {
            return await body(output, error).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Canceled.").ConfigureAwait(false);
            return Canceled;
        }
        catch (Exception ex) when (ex is WorkerStartException or WorkerCrashedException or WorkerOperationException)
        {
            await error.WriteLineAsync("Error: " + ex.Message).ConfigureAwait(false);
            return Failure;
        }
        catch (Exception ex)
        {
            // Last-chance boundary: a plain message for the user, the stack trace for the log (ARCHITECTURE.md 11).
            logger?.LogError(ex, "Command failed");
            await error.WriteLineAsync($"Error: {ex.Message} Details are in the log in {FileLoggerProvider.DefaultDirectory}.").ConfigureAwait(false);
            return Failure;
        }
    }

    private static string Mb(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("N1", CultureInfo.InvariantCulture) + " MB";

    private sealed class ProductVersionAction : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            parseResult.InvocationConfiguration.Output.WriteLine(ProductInfo.Version);
            return Success;
        }
    }

    /// <summary>Prints job progress to stderr, at most a few times per second.</summary>
    private sealed class ConsoleProgress : IDisposable
    {
        private readonly JobTracker _tracker;
        private readonly TrackedJob _job;
        private readonly TextWriter _error;
        private readonly object _gate = new();
        private DateTime _lastWrite = DateTime.MinValue;
        private string? _lastPhase;

        public ConsoleProgress(JobTracker tracker, TrackedJob job, TextWriter error)
        {
            _tracker = tracker;
            _job = job;
            _error = error;
            _tracker.JobChanged += OnJobChanged;
        }

        public void Complete() => _tracker.JobChanged -= OnJobChanged;

        public void Dispose() => Complete();

        private void OnJobChanged(object? sender, TrackedJob job)
        {
            if (job.Id != _job.Id || job.Progress is not { } progress)
            {
                return;
            }

            lock (_gate)
            {
                // A new phase is always printed (it can carry a warning, such as the paused target); repeats are throttled.
                var now = DateTime.UtcNow;
                if (progress.Phase == _lastPhase && now - _lastWrite < TimeSpan.FromMilliseconds(250))
                {
                    return;
                }

                _lastWrite = now;
                _lastPhase = progress.Phase;
                var percent = progress.Total is > 0 ? $" {100.0 * progress.Completed / progress.Total.Value,5:N1}%" : string.Empty;
                _error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{progress.Phase}{percent}{(progress.Message is { } message ? ": " + message : string.Empty)}"));
            }
        }
    }
}
