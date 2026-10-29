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
using System.Globalization;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

        var matchObjects = new Option<bool>("--match-objects") { Description = "Add New and Dead objects per row, matched by address and type: only for two dumps of one process (sprint 3.3). An object moved by a compacting GC counts as dead and new." };
        var retained = RetainedOption("Add each row's retained bytes in both dumps and the delta (lower bounds; computes each dump's dominators the first time).");
        var growthOf = new Option<string>("--growth-of") { Description = "What --fail-on-growth checks: total bytes (default) or one heap kind's bytes (gen0, gen1, gen2, loh, poh; with a size, not a percent).", DefaultValueFactory = _ => "total" };
        growthOf.AcceptOnlyFromAmong(s_growthColumns.Keys.ToArray());
        var perRow = new Option<bool>("--fail-on-growth-per-row") { Description = "Check --fail-on-growth against each matching row on its own (a percent against that row's baseline bytes), so one growing type cannot hide behind a shrinking one." };

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
        command.Options.Add(matchObjects);
        command.Options.Add(retained);
        command.Options.Add(growthOf);
        command.Options.Add(perRow);
        AddTopOrAllValidator(command, top, all);
        command.Validators.Add(result =>
        {
            // Interface rows overlap, so their growth adds up a type once per interface (the 3.2 review).
            if (result.GetValue(failOnGrowth) is not null && result.GetValue(group) == "interface")
            {
                result.AddError("--fail-on-growth cannot gate interface rows: a type counts under each interface it implements, so their growth adds up more than once. Gate by type, namespace or assembly.");
            }

            if (result.GetValue(failOnGrowth) is { } gate && gate.TrimEnd().EndsWith('%') && result.GetValue(growthOf) is not (null or "total") && !result.GetValue(perRow))
            {
                result.AddError("--growth-of a heap kind takes a size (50MB), not a percent: the dumps' per-kind baseline bytes are not summed per row.");
            }

            if ((result.GetValue(growthOf) is not (null or "total") || result.GetValue(perRow)) && result.GetValue(failOnGrowth) is null)
            {
                result.AddError("--growth-of and --fail-on-growth-per-row go with --fail-on-growth.");
            }

            if (result.GetValue(retained) && result.GetValue(group) == "interface")
            {
                result.AddError("Interface rows have no retained bytes: a type counts under each interface it implements. Leave out --retained, or group by type, namespace or assembly.");
            }
        });
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
                progress: parseResult.GetValue(retained) ? new StderrProgress(error) : null,
                ct,
                parseResult.GetValue(matchObjects),
                parseResult.GetValue(retained)).ConfigureAwait(false);
            var result = diff.Result;
            if (result.IdentityNote is { } why)
            {
                await error.WriteLineAsync("Note: no New and Dead objects: " + why).ConfigureAwait(false);
            }
            else if (result.ObjectsMatched)
            {
                await error.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Note: {result.NewObjects:N0} new and {result.DeadObjects:N0} dead objects, matched by address: an object moved by a compacting GC counts as dead and new.")).ConfigureAwait(false);
            }
            await WriteNotesAsync(error, "Baseline", before).ConfigureAwait(false);
            await WriteNotesAsync(error, "Current", after).ConfigureAwait(false);
            if (!result.ExactCounts)
            {
                await error.WriteLineAsync("Warning: at least one snapshot has estimated counts (a sampled gcdump), so the deltas are estimates too.").ConfigureAwait(false);
            }

            var wanted = parseResult.GetValue(all) ? result.Rows.TotalCount : Math.Min(parseResult.GetValue(top), result.Rows.TotalCount);
            var rows = await FetchRowsAsync(diff.Rows, wanted, ct).ConfigureAwait(false);
            var columns = diff.Rows.Info.Columns;

            // A per-row or per-kind gate reads every matching row, before the rows are released.
            var gateAll = parseResult.GetValue(failOnGrowth) is not null && (parseResult.GetValue(perRow) || parseResult.GetValue(growthOf) != "total");
            var gateRows = gateAll ? (wanted == result.Rows.TotalCount ? rows : await FetchRowsAsync(diff.Rows, result.Rows.TotalCount, ct).ConfigureAwait(false)) : rows;
            switch (parseResult.GetValue(format))
            {
                case "csv":
                    await WriteCsvAsync(output, columns, rows).ConfigureAwait(false);
                    break;
                case "json":
                    await WriteDiffJsonAsync(output, result, before, after, columns, rows).ConfigureAwait(false);
                    break;
                default:
                    await WriteDiffTableAsync(output, result, columns, rows).ConfigureAwait(false);
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

                var kindColumn = s_growthColumns[parseResult.GetValue(growthOf)!];
                if (kindColumn is not null && !columns.Any(c => c.Name == kindColumn))
                {
                    await error.WriteLineAsync("Error: --growth-of a heap kind needs both dumps to record generations (a gcdump does not).").ConfigureAwait(false);
                    return Failure;
                }

                long Cell(IReadOnlyList<string?> row, string column) => long.Parse(row[ColumnAt(columns, column)]!, CultureInfo.InvariantCulture);
                long delta, baselineBytes;
                var offenders = new List<string>();
                if (parseResult.GetValue(perRow))
                {
                    // Each row on its own (decision 5): its own delta against its own baseline bytes.
                    foreach (var row in gateRows)
                    {
                        var rowDelta = Cell(row, kindColumn ?? TypeDiffColumns.BytesDelta);
                        if (threshold.IsExceededBy(rowDelta, Cell(row, TypeDiffColumns.BaselineBytes)))
                        {
                            offenders.Add(string.Create(CultureInfo.InvariantCulture, $"{row[0]} ({Signed(rowDelta)} bytes)"));
                        }
                    }

                    (delta, baselineBytes) = (gateRows.Count == 0 ? 0 : gateRows.Max(r => Cell(r, kindColumn ?? TypeDiffColumns.BytesDelta)), 0);
                }
                else
                {
                    (delta, baselineBytes) = kindColumn is null
                        ? (result.MatchingBytesDelta, result.MatchingBaselineBytes)
                        : (gateRows.Sum(r => Cell(r, kindColumn)), 0);
                }

                var exceeded = parseResult.GetValue(perRow) ? offenders.Count > 0 : threshold.IsExceededBy(delta, baselineBytes);
                var limit = threshold.LimitFor(baselineBytes);

                // The gate's decision in the log, so a failed CI run can be explained later (sprint 2.3.7 review M4). The
                // filter is logged by its length only.
                services().GetRequiredService<ILoggerFactory>().CreateLogger("HeapScope.Cli.Diff").LogInformation(
                    "Growth gate {Outcome} ({Grouping} rows, growth of {GrowthOf}, per row {PerRow}): grew by {Delta} bytes from {BaselineBytes} bytes, limit {Threshold} ({Limit} bytes); {Offenders} rows over it; {Rows} matching rows, filter {FilterLength} chars; baseline {Baseline} (worker snapshot {BaselineId}), current {Current} (worker snapshot {CurrentId}); exact counts {ExactCounts}",
                    exceeded ? "failed" : "passed",
                    parseResult.GetValue(group),
                    parseResult.GetValue(growthOf),
                    parseResult.GetValue(perRow),
                    delta,
                    baselineBytes,
                    text,
                    limit,
                    offenders.Count,
                    result.Rows.TotalCount,
                    parseResult.GetValue(filter)?.Length ?? 0,
                    before.Path,
                    before.Descriptor.SnapshotId,
                    after.Path,
                    after.Descriptor.SnapshotId,
                    result.ExactCounts);
                if (exceeded)
                {
                    await error.WriteLineAsync(parseResult.GetValue(perRow)
                        ? string.Create(CultureInfo.InvariantCulture, $"{offenders.Count:N0} rows grew by more than {text}: {string.Join(", ", offenders.Take(10))}{(offenders.Count > 10 ? " and more" : string.Empty)}.")
                        : string.Create(CultureInfo.InvariantCulture, $"Grew by {Signed(delta)} bytes{(kindColumn is null ? string.Empty : " in " + parseResult.GetValue(growthOf))}, more than {text} ({limit:N0} bytes).")).ConfigureAwait(false);
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
        ["new"] = TypeDiffColumns.NewObjects,
        ["dead"] = TypeDiffColumns.DeadObjects,
        ["retained-delta"] = TypeDiffColumns.RetainedDelta,
        ["gen2-delta"] = TypeDiffColumns.Gen2Delta,
    };

    private static string SortChoices => string.Join(", ", s_sortColumns.Keys);

    /// <summary>The index of a result column, or -1.</summary>
    private static int ColumnAt(IReadOnlyList<ResultColumn> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>What <c>--growth-of</c> checks: null for total bytes, else a per-kind delta column (sprint 3.3).</summary>
    private static readonly Dictionary<string, string?> s_growthColumns = new(StringComparer.Ordinal)
    {
        ["total"] = null,
        ["gen0"] = TypeDiffColumns.Gen0Delta,
        ["gen1"] = TypeDiffColumns.Gen1Delta,
        ["gen2"] = TypeDiffColumns.Gen2Delta,
        ["loh"] = TypeDiffColumns.LohDelta,
        ["poh"] = TypeDiffColumns.PohDelta,
    };

    private static async Task WriteDiffTableAsync(TextWriter output, TypeDiffResult result, IReadOnlyList<ResultColumn> columns, List<IReadOnlyList<string?>> rows)
    {
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Baseline {result.BaselineObjects:N0} objects, {Mb(result.BaselineBytes)}; current {result.CurrentObjects:N0} objects, {Mb(result.CurrentBytes)}; delta {Signed(result.CurrentObjects - result.BaselineObjects)} objects, {Signed(result.CurrentBytes - result.BaselineBytes)} bytes (every object each snapshot holds: a full dump includes unreachable ones, a gcdump only survivors of its collection)")).ConfigureAwait(false);
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{result.Rows.TotalCount:N0} of {result.UnfilteredRows:N0} rows match ({result.NewRows:N0} new, {result.GoneRows:N0} gone overall), showing {rows.Count:N0}; matching rows grew by {Signed(result.MatchingBytesDelta)} bytes")).ConfigureAwait(false);
        // Sprint 3.3: New and Dead, and the retained delta, when the diff has them.
        var (newAt, deadAt, retainedAt) = (ColumnAt(columns, TypeDiffColumns.NewObjects), ColumnAt(columns, TypeDiffColumns.DeadObjects), ColumnAt(columns, TypeDiffColumns.RetainedDelta));
        var extraHeader = (newAt >= 0 ? $" {"New",10} {"Dead",10}" : string.Empty) + (retainedAt >= 0 ? $" {"Retained delta",16}" : string.Empty);
        await output.WriteLineAsync($"{"Objects delta",14} {"Bytes delta",16} {"Objects",12} {"Bytes",16}{extraHeader} {"Change",6}  Name").ConfigureAwait(false);
        foreach (var row in rows)
        {
            var extra = (newAt >= 0 ? string.Create(CultureInfo.InvariantCulture, $" {long.Parse(row[newAt]!, CultureInfo.InvariantCulture),10:N0} {long.Parse(row[deadAt]!, CultureInfo.InvariantCulture),10:N0}") : string.Empty)
                + (retainedAt >= 0 ? string.Create(CultureInfo.InvariantCulture, $" {Signed(long.Parse(row[retainedAt]!, CultureInfo.InvariantCulture)),16}") : string.Empty);
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{Signed(long.Parse(row[4]!, CultureInfo.InvariantCulture)),14} {Signed(long.Parse(row[7]!, CultureInfo.InvariantCulture)),16} {long.Parse(row[3]!, CultureInfo.InvariantCulture),12:N0} {long.Parse(row[6]!, CultureInfo.InvariantCulture),16:N0}{extra} {row[1],6}  {row[0]}")).ConfigureAwait(false);
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
            BaselineKind = baseline.Descriptor.Kind.ToString(),
            CurrentKind = current.Descriptor.Kind.ToString(),

            // A full dump counts dead objects not yet collected; a gcdump only the survivors of its collection (1.9). Each
            // side says so for itself; IncludesUnreachable is true only when both do.
            BaselineIncludesUnreachable = baseline.Descriptor.Kind == SnapshotKindDto.FullDump,
            CurrentIncludesUnreachable = current.Descriptor.Kind == SnapshotKindDto.FullDump,
            IncludesUnreachable = baseline.Descriptor.Kind == SnapshotKindDto.FullDump && current.Descriptor.Kind == SnapshotKindDto.FullDump,
            result.NewRows,
            result.GoneRows,
            result.UnfilteredRows,
            MatchingRows = result.Rows.TotalCount,
            result.MatchingBaselineBytes,
            result.MatchingBytesDelta,
            Rows = JsonRows(columns, rows),
        };
        await WriteJsonAsync(output, document).ConfigureAwait(false);
    }

    private static string Signed(long value) => value.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture);
}
