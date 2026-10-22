using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using HeapScope.Application;
using HeapScope.Application.Jobs;
using HeapScope.Application.Logging;
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

    private static Func<IServiceProvider>? s_services;

    public static RootCommand Build(Func<IServiceProvider> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        s_services = services;
        var root = new RootCommand($"{ProductInfo.Name} {ProductInfo.Version}: .NET memory profiler command line");
        root.Options.Add(PipeOption);

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
        return root;
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
                var now = DateTime.UtcNow;
                if (now - _lastWrite < TimeSpan.FromMilliseconds(250))
                {
                    return;
                }

                _lastWrite = now;
                var percent = progress.Total is > 0 ? $" {100.0 * progress.Completed / progress.Total.Value,5:N1}%" : string.Empty;
                _error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{progress.Phase}{percent}"));
            }
        }
    }
}
