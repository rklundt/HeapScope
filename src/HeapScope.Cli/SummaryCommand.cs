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
using HeapScope.Application.Presentation;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;

namespace HeapScope.Cli;

/// <summary>The <c>summary</c> command: a dump's objects and bytes by type, namespace, assembly or interface.</summary>
internal static partial class CliCommands
{
    private static Command BuildSummaryCommand(Func<IServiceProvider> services)
    {
        var dump = new Argument<FileInfo>("dump") { Description = "Path of the full memory dump (.dmp)." };
        var group = GroupOption();
        var filter = FilterOption();
        var sort = new Option<string>("--sort") { Description = "Sort by bytes, count, name or retained (adds the retained column).", DefaultValueFactory = _ => "bytes" };
        sort.AcceptOnlyFromAmong("bytes", "count", "name", "retained");
        var retained = RetainedOption("Add retained bytes per row: a lower bound, the bytes its objects keep alive that no other object of the row holds (computes the snapshot's dominators the first time, then reuses them).");
        var top = TopOption();
        var all = AllOption();
        var format = FormatOption();

        var command = new Command("summary", "Summarize a dump by type, namespace, assembly or interface (opens it first; reuses its index cache). Ctrl+C cancels.");
        command.Arguments.Add(dump);
        command.Options.Add(group);
        command.Options.Add(filter);
        command.Options.Add(sort);
        command.Options.Add(retained);
        command.Options.Add(top);
        command.Options.Add(all);
        command.Options.Add(format);
        AddTopOrAllValidator(command, top, all);
        command.Validators.Add(result =>
        {
            if (result.GetValue(group) == "interface" && (result.GetValue(retained) || result.GetValue(sort) == "retained"))
            {
                result.AddError("Interface rows have no retained bytes: a type counts under each interface it implements, so the rows overlap. Leave out --retained (and --sort retained), or group by type, namespace or assembly.");
            }
        });
        command.SetAction((parseResult, ct) => RunAsync(parseResult, services, async context =>
        {
            var (output, error) = (context.Output, context.Error);
            var handle = await OpenForQueryAsync(context, parseResult.GetValue(dump)!, ct).ConfigureAwait(false);
            if (handle is null)
            {
                return Failure;
            }

            var session = context.Get<SnapshotSession>();
            if (parseResult.GetValue(group) == "interface")
            {
                await error.WriteLineAsync("Note: a type counts under each interface it implements, so interface rows overlap and add up to more than the total.").ConfigureAwait(false);
            }

            var sortName = parseResult.GetValue(sort);
            var progress = parseResult.GetValue(retained) || sortName == "retained" ? new StderrProgress(error) : null;
            var summary = await session.QueryTypeSummaryAsync(
                handle,
                Enum.Parse<TypeGrouping>(parseResult.GetValue(group)!, ignoreCase: true),
                parseResult.GetValue(filter),
                sortName switch { "name" => TypeSummaryColumns.Name, "count" => TypeSummaryColumns.Objects, "retained" => TypeSummaryColumns.Retained, _ => TypeSummaryColumns.Bytes },
                descending: sortName != "name",
                includeRetained: parseResult.GetValue(retained) || sortName == "retained",
                progress: progress,
                ct).ConfigureAwait(false);
            progress?.Stop();
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
                    await WriteSummaryTableAsync(output, summary.Result, handle.Descriptor.Kind, columns, rows).ConfigureAwait(false);
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

    internal static async Task WriteSummaryTableAsync(TextWriter output, TypeSummaryResult result, SnapshotKindDto kind, IReadOnlyList<ResultColumn> columns, List<IReadOnlyList<string?>> rows)
    {
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{result.TotalObjects:N0} objects, {Mb(result.TotalBytes)}, {result.TotalTypes:N0} types ({SnapshotScopeTexts.Snapshot(kind)}); {result.Rows.TotalCount:N0} of {result.UnfilteredRows:N0} rows match, showing {rows.Count:N0}")).ConfigureAwait(false);
        if (result.HasGenerations)
        {
            await output.WriteLineAsync("Heap kinds: " + string.Join(", ", result.HeapKinds.Select(k => string.Create(CultureInfo.InvariantCulture, $"{k.Kind} {Mb(k.Bytes)}")))).ConfigureAwait(false);
        }

        var cells = new ColumnMap(columns);
        if (result.Dominators is { } dominators)
        {
            var bound = result.Rows.Columns.FirstOrDefault(c => c.Name == TypeSummaryColumns.Retained)?.Bound;
            await output.WriteLineAsync(RetainedNote(dominators) + (bound == ValueBound.LowerBound ? " Retained per row: " + ValueBoundTexts.LowerBound : string.Empty)).ConfigureAwait(false);
            await output.WriteLineAsync($"{"Objects",14} {"Bytes",16} {"%",7} {"Retained",16}  Name").ConfigureAwait(false);
            foreach (var row in rows)
            {
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{cells.Long(row, TypeSummaryColumns.Objects),14:N0} {cells.Long(row, TypeSummaryColumns.Bytes),16:N0} {cells.Text(row, TypeSummaryColumns.Percent),7} {cells.Long(row, TypeSummaryColumns.Retained),16:N0}  {cells.Text(row, TypeSummaryColumns.Name)}")).ConfigureAwait(false);
            }

            return;
        }

        await output.WriteLineAsync($"{"Objects",14} {"Bytes",16} {"%",7}  Name").ConfigureAwait(false);
        foreach (var row in rows)
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{cells.Long(row, TypeSummaryColumns.Objects),14:N0} {cells.Long(row, TypeSummaryColumns.Bytes),16:N0} {cells.Text(row, TypeSummaryColumns.Percent),7}  {cells.Text(row, TypeSummaryColumns.Name)}")).ConfigureAwait(false);
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
            // From the column itself (sprint 2.4, 2.3.7 review M7).
            RetainedIsLowerBound = result.Dominators is null ? (bool?)null : result.Rows.Columns.FirstOrDefault(c => c.Name == TypeSummaryColumns.Retained)?.Bound == ValueBound.LowerBound,
            result.Dominators,
            Rows = JsonRows(columns, rows),
        };
        await WriteJsonAsync(output, document).ConfigureAwait(false);
    }
}
