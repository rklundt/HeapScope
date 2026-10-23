using System.CommandLine;
using System.Globalization;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HeapScope.Cli;

/// <summary>The <c>diff</c> command (sprint 1.4, ADR-018): growth by type between two dumps, with a CI threshold.</summary>
internal static partial class CliCommands
{
    private static Command BuildDiffCommand(Func<IServiceProvider> services)
    {
        var baseline = new Argument<FileInfo>("baseline") { Description = "The earlier dump (.dmp); deltas are current minus baseline." };
        var current = new Argument<FileInfo>("current") { Description = "The later dump (.dmp)." };
        var group = GroupOption();
        var filter = FilterOption(" --fail-on-growth checks only the matching rows.");
        var sort = new Option<string>("--sort") { Description = "Sort column, descending: " + SortChoices + ".", DefaultValueFactory = _ => "bytes-delta" };
        sort.AcceptOnlyFromAmong(s_sortColumns.Keys.ToArray());
        var top = TopOption();
        var all = AllOption();
        var format = FormatOption();
        var failOnGrowth = new Option<string?>("--fail-on-growth")
        {
            Description = "Exit with code 2 when the matching rows grew by more than this: a size (50MB, 512KB, 1GB, or bytes) "
                + "or a percent of their baseline bytes (10%; anything growing from zero bytes exceeds every percent). "
                + "Dumps count unreachable objects not yet collected, so for a stable gate capture both after a full GC "
                + "in the target (GC.Collect, GC.WaitForPendingFinalizers, GC.Collect).",
        };
        failOnGrowth.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<string?>() is { } text && !GrowthThreshold.TryParse(text, out _))
            {
                result.AddError($"--fail-on-growth: '{text}' is not a size (50MB, 512KB, 1GB, 1048576) or a percent (10%).");
            }
        });

        var command = new Command("diff", "Compare two dumps by type: objects and bytes in each and their growth (opens them first; reuses their index caches). Exit codes: 0 ok, 2 grew more than --fail-on-growth, 1 error, 64 usage, 130 canceled.");
        command.Arguments.Add(baseline);
        command.Arguments.Add(current);
        command.Options.Add(group);
        command.Options.Add(filter);
        command.Options.Add(sort);
        command.Options.Add(top);
        command.Options.Add(all);
        command.Options.Add(format);
        command.Options.Add(failOnGrowth);
        AddTopOrAllValidator(command, top, all);
        command.SetAction((parseResult, ct) => RunAsync(parseResult, async (output, error) =>
        {
            var before = await OpenForQueryAsync(parseResult.GetValue(baseline)!, error, ct).ConfigureAwait(false);
            if (before is null)
            {
                return Failure;
            }

            var after = await OpenForQueryAsync(parseResult.GetValue(current)!, error, ct).ConfigureAwait(false);
            if (after is null)
            {
                return Failure;
            }

            var session = services().GetRequiredService<SnapshotSession>();
            var sortName = parseResult.GetValue(sort)!;
            var diff = await session.QueryTypeDiffAsync(
                before,
                after,
                Enum.Parse<TypeGrouping>(parseResult.GetValue(group)!, ignoreCase: true),
                parseResult.GetValue(filter),
                s_sortColumns[sortName],
                descending: sortName != "name",
                progress: null,
                ct).ConfigureAwait(false);
            var result = diff.Result;
            await WriteNotesAsync(error, "Baseline", before).ConfigureAwait(false);
            await WriteNotesAsync(error, "Current", after).ConfigureAwait(false);
            if (!result.ExactCounts)
            {
                await error.WriteLineAsync("Warning: at least one snapshot has estimated counts (a sampled gcdump), so the deltas are estimates too.").ConfigureAwait(false);
            }

            var wanted = parseResult.GetValue(all) ? result.Rows.TotalCount : Math.Min(parseResult.GetValue(top), result.Rows.TotalCount);
            var rows = await FetchRowsAsync(diff.Rows, wanted, ct).ConfigureAwait(false);
            var columns = diff.Rows.Info.Columns;
            switch (parseResult.GetValue(format))
            {
                case "csv":
                    await WriteCsvAsync(output, columns, rows).ConfigureAwait(false);
                    break;
                case "json":
                    await WriteDiffJsonAsync(output, result, before, after, columns, rows).ConfigureAwait(false);
                    break;
                default:
                    await WriteDiffTableAsync(output, result, rows).ConfigureAwait(false);
                    break;
            }

            // Output first: a worker gone at release time must not cost the rows already fetched.
            try
            {
                await diff.Rows.ReleaseAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Application.Workers.WorkerCrashedException or Application.Workers.WorkerOperationException)
            {
                // Releasing is housekeeping in a process about to exit; the rows are out.
            }

            if (parseResult.GetValue(failOnGrowth) is { } text && GrowthThreshold.TryParse(text, out var threshold))
            {
                // A gate whose filter matches nothing checks nothing: a typo or a renamed type must not pass forever.
                if (result.Rows.TotalCount == 0 && !string.IsNullOrWhiteSpace(parseResult.GetValue(filter)))
                {
                    await error.WriteLineAsync($"Error: --filter '{parseResult.GetValue(filter)}' matches no rows, so --fail-on-growth has nothing to check. Fix the filter.").ConfigureAwait(false);
                    return Failure;
                }

                var limit = threshold.LimitFor(result.MatchingBaselineBytes);
                if (threshold.IsExceededBy(result.MatchingBytesDelta, result.MatchingBaselineBytes))
                {
                    await error.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"Grew by {Signed(result.MatchingBytesDelta)} bytes, more than {text} ({limit:N0} bytes).")).ConfigureAwait(false);
                    return GrowthExceeded;
                }
            }

            return Success;
        }));
        return command;
    }

    /// <summary>CLI sort names and the result columns they sort by.</summary>
    private static readonly Dictionary<string, string> s_sortColumns = new(StringComparer.Ordinal)
    {
        ["bytes-delta"] = TypeDiffColumns.BytesDelta,
        ["count-delta"] = TypeDiffColumns.ObjectsDelta,
        ["bytes"] = TypeDiffColumns.CurrentBytes,
        ["count"] = TypeDiffColumns.CurrentObjects,
        ["baseline-bytes"] = TypeDiffColumns.BaselineBytes,
        ["baseline-count"] = TypeDiffColumns.BaselineObjects,
        ["name"] = TypeDiffColumns.Name,
    };

    private static string SortChoices => string.Join(", ", s_sortColumns.Keys);

    private static async Task WriteDiffTableAsync(TextWriter output, TypeDiffResult result, List<IReadOnlyList<string?>> rows)
    {
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Baseline {result.BaselineObjects:N0} objects, {Mb(result.BaselineBytes)}; current {result.CurrentObjects:N0} objects, {Mb(result.CurrentBytes)}; delta {Signed(result.CurrentObjects - result.BaselineObjects)} objects, {Signed(result.CurrentBytes - result.BaselineBytes)} bytes (all objects in each dump, unreachable ones included)")).ConfigureAwait(false);
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{result.Rows.TotalCount:N0} of {result.UnfilteredRows:N0} rows match ({result.NewRows:N0} new, {result.GoneRows:N0} gone overall), showing {rows.Count:N0}; matching rows grew by {Signed(result.MatchingBytesDelta)} bytes")).ConfigureAwait(false);
        await output.WriteLineAsync($"{"Objects delta",14} {"Bytes delta",16} {"Objects",12} {"Bytes",16} {"Change",6}  Name").ConfigureAwait(false);
        foreach (var row in rows)
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{Signed(long.Parse(row[4]!, CultureInfo.InvariantCulture)),14} {Signed(long.Parse(row[7]!, CultureInfo.InvariantCulture)),16} {long.Parse(row[3]!, CultureInfo.InvariantCulture),12:N0} {long.Parse(row[6]!, CultureInfo.InvariantCulture),16:N0} {row[1],6}  {row[0]}")).ConfigureAwait(false);
        }
    }

    private static async Task WriteDiffJsonAsync(
        TextWriter output,
        TypeDiffResult result,
        SnapshotHandle baseline,
        SnapshotHandle current,
        IReadOnlyList<ResultColumn> columns,
        List<IReadOnlyList<string?>> rows)
    {
        var document = new
        {
            result.ExactCounts,
            BaselineNotes = baseline.Descriptor.Notes,
            CurrentNotes = current.Descriptor.Notes,
            result.BaselineObjects,
            result.BaselineBytes,
            result.CurrentObjects,
            result.CurrentBytes,
            IncludesUnreachable = true,
            result.NewRows,
            result.GoneRows,
            result.UnfilteredRows,
            MatchingRows = result.Rows.TotalCount,
            result.MatchingBaselineBytes,
            result.MatchingBytesDelta,
            Rows = JsonRows(columns, rows),
        };
        await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(document, s_json)).ConfigureAwait(false);
    }

    private static string Signed(long value) => value.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture);
}
