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
/// Builds the command tree (ADR-008; inspection commands in InspectCommands.cs). Exit codes: 0 success, 1 failure, 2 `diff --fail-on-growth` threshold exceeded,
/// 64 usage error (EX_USAGE), 130 canceled.
/// </summary>
internal static partial class CliCommands
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int GrowthExceeded = 2;
    public const int Usage = 64;
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
        root.Subcommands.Add(BuildSummaryCommand(services));
        root.Subcommands.Add(BuildDiffCommand(services));
        root.Subcommands.Add(BuildInstancesCommand(services));
        root.Subcommands.Add(BuildObjectCommand(services));
        root.Subcommands.Add(BuildReferrersCommand(services));
        root.Subcommands.Add(BuildInspectCommand(services));
        root.Subcommands.Add(BuildTraceCommand(services));
        root.Subcommands.Add(BuildMonitorCommand(services));
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
        var open = new Option<bool>("--open") { Description = "Open the snapshot after capturing it and print its summary." };
        var kind = KindOption();
        var command = new Command("capture", "Capture a full memory dump (the process is paused while it is written) or, with --kind gcdump, a gcdump (the process runs one full GC). Ctrl+C cancels.");
        command.Options.Add(pid);
        command.Options.Add(output);
        command.Options.Add(sessionOption);
        command.Options.Add(label);
        command.Options.Add(open);
        command.Options.Add(kind);
        command.SetAction((parseResult, ct) => RunAsync(parseResult, async (stdout, error) =>
        {
            var capture = services().GetRequiredService<ICaptureService>();
            if (parseResult.GetValue(sessionOption) is { } sessionName && capture.Library.Open(sessionName) is null)
            {
                await error.WriteLineAsync($"Error: {sessionName} is not a HeapScope session under {capture.Library.Root}.").ConfigureAwait(false);
                return Failure;
            }

            var jobs = services().GetRequiredService<JobTracker>();
            var job = capture.StartCapture(parseResult.GetValue(pid), parseResult.GetValue(output)?.FullName, parseResult.GetValue(label), kind: KindOf(parseResult.GetValue(kind)));
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

    /// <summary>What a capture writes (sprint 1.8): a full dump (the default) or a gcdump.</summary>
    internal static Option<string> KindOption()
    {
        var kind = new Option<string>("--kind") { Description = "What to capture: full (a full memory dump) or gcdump (the object graph only: smaller, no field values or string text).", DefaultValueFactory = _ => "full" };
        kind.AcceptOnlyFromAmong("full", "gcdump");
        return kind;
    }

    internal static SnapshotKindDto KindOf(string? kind) => kind == "gcdump" ? SnapshotKindDto.GcDump : SnapshotKindDto.FullDump;

    private static Command BuildSummaryCommand(Func<IServiceProvider> services)
    {
        var dump = new Argument<FileInfo>("dump") { Description = "Path of the full memory dump (.dmp)." };
        var group = GroupOption();
        var filter = FilterOption();
        var sort = new Option<string>("--sort") { Description = "Sort by bytes, count or name.", DefaultValueFactory = _ => "bytes" };
        sort.AcceptOnlyFromAmong("bytes", "count", "name");
        var top = TopOption();
        var all = AllOption();
        var format = FormatOption();

        var command = new Command("summary", "Summarize a dump by type, namespace or assembly (opens it first; reuses its index cache). Ctrl+C cancels.");
        command.Arguments.Add(dump);
        command.Options.Add(group);
        command.Options.Add(filter);
        command.Options.Add(sort);
        command.Options.Add(top);
        command.Options.Add(all);
        command.Options.Add(format);
        AddTopOrAllValidator(command, top, all);
        command.SetAction((parseResult, ct) => RunAsync(parseResult, async (output, error) =>
        {
            var handle = await OpenForQueryAsync(parseResult.GetValue(dump)!, error, ct).ConfigureAwait(false);
            if (handle is null)
            {
                return Failure;
            }

            var session = services().GetRequiredService<SnapshotSession>();
            var summary = await session.QueryTypeSummaryAsync(
                handle,
                Enum.Parse<TypeGrouping>(parseResult.GetValue(group)!, ignoreCase: true),
                parseResult.GetValue(filter),
                parseResult.GetValue(sort) switch { "name" => TypeSummaryColumns.Name, "count" => TypeSummaryColumns.Objects, _ => TypeSummaryColumns.Bytes },
                descending: parseResult.GetValue(sort) != "name",
                progress: null,
                ct).ConfigureAwait(false);
            await WriteNotesAsync(error, "Snapshot", handle).ConfigureAwait(false);
            var wanted = parseResult.GetValue(all) ? summary.Result.Rows.TotalCount : Math.Min(parseResult.GetValue(top), summary.Result.Rows.TotalCount);
            var rows = await FetchRowsAsync(summary.Rows, wanted, ct).ConfigureAwait(false);
            var columns = summary.Rows.Info.Columns;
            switch (parseResult.GetValue(format))
            {
                case "csv":
                    await WriteCsvAsync(output, columns, rows).ConfigureAwait(false);
                    break;
                case "json":
                    await WriteSummaryJsonAsync(output, summary.Result, handle.Descriptor.Kind, columns, rows).ConfigureAwait(false);
                    break;
                default:
                    await WriteSummaryTableAsync(output, summary.Result, rows).ConfigureAwait(false);
                    break;
            }

            // Output first: a worker gone at release time must not cost the rows already fetched.
            try
            {
                await summary.Rows.ReleaseAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WorkerCrashedException or WorkerOperationException)
            {
                // Releasing is housekeeping in a process about to exit; the rows are out.
            }

            return Success;
        }));
        return command;
    }

    private static async Task WriteSummaryTableAsync(TextWriter output, TypeSummaryResult result, List<IReadOnlyList<string?>> rows)
    {
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{result.TotalObjects:N0} objects, {Mb(result.TotalBytes)}, {result.TotalTypes:N0} types (all objects in the dump, including unreachable ones not yet collected); {result.Rows.TotalCount:N0} of {result.UnfilteredRows:N0} rows match, showing {rows.Count:N0}")).ConfigureAwait(false);
        if (result.HasGenerations)
        {
            await output.WriteLineAsync("Heap kinds: " + string.Join(", ", result.HeapKinds.Select(k => string.Create(CultureInfo.InvariantCulture, $"{k.Kind} {Mb(k.Bytes)}")))).ConfigureAwait(false);
        }

        await output.WriteLineAsync($"{"Objects",14} {"Bytes",16} {"%",7}  Name").ConfigureAwait(false);
        foreach (var row in rows)
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{long.Parse(row[1]!, CultureInfo.InvariantCulture),14:N0} {long.Parse(row[2]!, CultureInfo.InvariantCulture),16:N0} {row[3],7}  {row[0]}")).ConfigureAwait(false);
        }
    }

    private static async Task WriteSummaryJsonAsync(TextWriter output, TypeSummaryResult result, SnapshotKindDto kind, IReadOnlyList<ResultColumn> columns, List<IReadOnlyList<string?>> rows)
    {
        var document = new
        {
            result.TotalObjects,
            result.TotalBytes,
            result.TotalTypes,
            Kind = kind.ToString(),

            // A full dump counts dead objects not yet collected; a gcdump only the survivors of its collection (1.9).
            IncludesUnreachable = kind == SnapshotKindDto.FullDump,
            result.UnfilteredRows,
            MatchingRows = result.Rows.TotalCount,
            HeapKinds = result.HeapKinds.Select(k => new { Kind = k.Kind.ToString(), k.Objects, k.Bytes }),
            Rows = JsonRows(columns, rows),
        };
        await WriteJsonAsync(output, document).ConfigureAwait(false);
    }

    private static Option<string> GroupOption()
    {
        var group = new Option<string>("--group") { Description = "Rows per type, namespace or assembly.", DefaultValueFactory = _ => "type" };
        group.AcceptOnlyFromAmong("type", "namespace", "assembly");
        return group;
    }

    private static Option<string?> FilterOption(string more = "") => new("--filter")
    {
        Description = "Keep rows whose name matches: lower case ignores case; capitals match as typed or as CamelHumps (SCE finds StaticCacheEntry); * matches anything." + more,
    };

    private static Option<int> TopOption()
    {
        var top = new Option<int>("--top") { Description = "Number of rows.", DefaultValueFactory = _ => 25 };
        top.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() < 1)
            {
                result.AddError("--top must be 1 or more.");
            }
        });
        return top;
    }

    private static Option<bool> AllOption() => new("--all") { Description = "Print every row." };

    private static Option<string> FormatOption()
    {
        var format = new Option<string>("--format") { Description = "Output: table, csv or json.", DefaultValueFactory = _ => "table" };
        format.AcceptOnlyFromAmong("table", "csv", "json");
        return format;
    }

    private static void AddTopOrAllValidator(Command command, Option<int> top, Option<bool> all) =>
        command.Validators.Add(result =>
        {
            if (result.GetResult(all) is { Implicit: false } && result.GetResult(top) is { Implicit: false })
            {
                result.AddError("Use either --top or --all, not both.");
            }
        });

    /// <summary>Prints a snapshot's notes (incomplete segments, dropped references) to stderr, labeled by its role.</summary>
    private static async Task WriteNotesAsync(TextWriter error, string role, SnapshotHandle snapshot)
    {
        foreach (var note in snapshot.Descriptor.Notes)
        {
            await error.WriteLineAsync($"{role} ({Path.GetFileName(snapshot.Path)}): {note}").ConfigureAwait(false);
        }
    }

    /// <summary>Opens a dump through the session with progress on stderr, or returns null after printing why not.</summary>
    private static async Task<SnapshotHandle?> OpenForQueryAsync(FileInfo file, TextWriter error, CancellationToken cancellationToken)
    {
        if (!file.Exists)
        {
            await error.WriteLineAsync($"Error: the file {file.FullName} does not exist.").ConfigureAwait(false);
            return null;
        }

        var session = s_services!().GetRequiredService<SnapshotSession>();
        var jobs = s_services!().GetRequiredService<JobTracker>();
        if (await session.GetOpenWarningAsync(file.FullName, rebuild: false, cancellationToken).ConfigureAwait(false) is { } warning)
        {
            await error.WriteLineAsync("Warning: " + warning).ConfigureAwait(false);
        }

        var openJob = session.StartOpen(file.FullName);
        using (new ConsoleProgress(jobs, openJob, error))
        using (cancellationToken.Register(openJob.Cancel))
        {
            return await openJob.Result.ConfigureAwait(false);
        }
    }

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

    internal sealed class ProductVersionAction : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            var output = parseResult.InvocationConfiguration.Output;
            output.WriteLine(ProductInfo.Version);
            output.WriteLine(ProductInfo.AttributionWithUrl); // ADDITIONAL-TERMS.md 1.4
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
