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

namespace HeapScope.Cli;

/// <summary>
/// The <c>inspect</c> command (sprint 1.6, ADR-020): the simple inspections of a dump. Without a name it runs every
/// inspection and prints one summary line each; with a name it prints that inspection's cause, fix and rows.
/// </summary>
internal static partial class CliCommands
{
    private static Command BuildInspectCommand(Func<IServiceProvider> services)
    {
        var dump = new Argument<FileInfo>("dump") { Description = "Path of the full memory dump (.dmp)." };
        var name = new Argument<string?>("name")
        {
            Description = "The inspection: duplicate-strings, empty-collections, oversized-collections, sparse-arrays, large-arrays, loh, finalizable, fragmentation, or a leak detector: growing-static-collections, event-handler-leaks, timer-leaks, disposed-but-referenced, httpclient-instances, undisposed-service-scopes. Leave out to run all and print a summary.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var top = TopOption();
        var all = AllOption();
        var format = FormatOption();
        var baselineDump = new Option<FileInfo?>("--baseline") { Description = "An earlier dump of the same process: the leak detectors that measure growth (growing-static-collections) compare with it (sprint 2.6)." };

        var command = new Command("inspect", "Find common memory waste (duplicate strings, empty and oversized collections, sparse and large arrays, the large object heap, finalizable objects, fragmentation) and name leaks (growing static collections, event handlers, timers, disposed but referenced objects, HttpClient instances, undisposed service scopes). Opens the dump first and reuses its index cache. Exit codes: 0 ok, 1 error (an unknown or unavailable inspection), 64 usage, 130 canceled.");
        command.Arguments.Add(dump);
        command.Arguments.Add(name);
        command.Options.Add(baselineDump);
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
            await WriteNotesAsync(error, "Snapshot", handle).ConfigureAwait(false);
            SnapshotHandle? baseline = null;
            if (parseResult.GetValue(baselineDump) is { } earlier)
            {
                baseline = await OpenForQueryAsync(earlier, error, ct).ConfigureAwait(false);
                if (baseline is null)
                {
                    return Failure;
                }

                await WriteNotesAsync(error, "Baseline", baseline).ConfigureAwait(false);
            }

            var inspections = await session.ListInspectionsAsync(handle, ct).ConfigureAwait(false);
            if (parseResult.GetValue(name) is not { } chosen)
            {
                return await WriteInspectionSummaryAsync(session, handle, inspections, parseResult.GetValue(format)!, output, ct, baseline).ConfigureAwait(false);
            }

            var inspection = inspections.FirstOrDefault(i => string.Equals(i.Name, chosen, StringComparison.OrdinalIgnoreCase));
            if (inspection is null)
            {
                await error.WriteLineAsync($"Error: there is no inspection {chosen}. Inspections: {string.Join(", ", inspections.Select(i => i.Name))}.").ConfigureAwait(false);
                return Failure;
            }

            if (inspection.Unavailable is { } why)
            {
                await error.WriteLineAsync($"Error: {inspection.Title} is not available for this snapshot. {why}").ConfigureAwait(false);
                return Failure;
            }

            var run = await session.RunInspectionAsync(handle, inspection.Name, progress: null, ct, baseline).ConfigureAwait(false);
            var result = run.Result;
            if (result.Note is { } note)
            {
                await error.WriteLineAsync("Note: " + note).ConfigureAwait(false);
            }

            var wanted = parseResult.GetValue(all) ? result.Rows.TotalCount : Math.Min(parseResult.GetValue(top), result.Rows.TotalCount);
            var rows = await FetchRowsAsync(run.Rows, wanted, ct).ConfigureAwait(false);
            var columns = run.Rows.Info.Columns;
            switch (parseResult.GetValue(format))
            {
                case "csv":
                    await WriteCsvAsync(output, columns, rows).ConfigureAwait(false);
                    break;
                case "json":
                    await WriteJsonAsync(output, new
                        {
                            inspection.Name,
                            inspection.Kind,
                            inspection.Title,
                            inspection.Cause,
                            inspection.Fix,
                            Baseline = result.BaselineSnapshotId is null ? null : baseline?.Path,
                            result.Count,
                            result.Bytes,
                            result.WastedBytes,
                            result.Note,
                            Rows = JsonRows(columns, rows),
                        }).ConfigureAwait(false);
                    break;
                default:
                    await output.WriteLineAsync($"{inspection.Title}: {SummaryText(result)}").ConfigureAwait(false);
                    await output.WriteLineAsync("Cause: " + inspection.Cause).ConfigureAwait(false);
                    await output.WriteLineAsync("Fix:   " + inspection.Fix).ConfigureAwait(false);
                    await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Showing {rows.Count:N0} of {result.Rows.TotalCount:N0} rows")).ConfigureAwait(false);
                    await WriteGridAsync(output, columns, rows).ConfigureAwait(false);
                    break;
            }

            await ReleaseQuietlyAsync(run.Rows, ct).ConfigureAwait(false);
            return Success;
        }));
        return command;
    }

    private static async Task<int> WriteInspectionSummaryAsync(SnapshotSession session, SnapshotHandle handle, IReadOnlyList<InspectionInfo> inspections, string format, TextWriter output, CancellationToken ct, SnapshotHandle? baseline = null)
    {
        var lines = new List<(InspectionInfo Info, InspectionResultDto? Result)>();
        foreach (var inspection in inspections)
        {
            if (inspection.Unavailable is not null)
            {
                lines.Add((inspection, null));
                continue;
            }

            var run = await session.RunInspectionAsync(handle, inspection.Name, progress: null, ct, baseline).ConfigureAwait(false);
            lines.Add((inspection, run.Result));
            await ReleaseQuietlyAsync(run.Rows, ct).ConfigureAwait(false);
        }

        if (format == "json")
        {
            await WriteJsonAsync(output, lines.Select(l => new
                {
                    l.Info.Name,
                    l.Info.Title,
                    l.Info.Unavailable,
                    l.Result?.Count,
                    l.Result?.Bytes,
                    l.Result?.WastedBytes,
                    l.Result?.Note,
                })).ConfigureAwait(false);
            return Success;
        }

        if (format == "csv")
        {
            await output.WriteLineAsync("Name,Count,Bytes,WastedBytes,Unavailable").ConfigureAwait(false);
            foreach (var (info, result) in lines)
            {
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{info.Name},{result?.Count},{result?.Bytes},{result?.WastedBytes},\"{info.Unavailable?.Replace("\"", "\"\"", StringComparison.Ordinal)}\"")).ConfigureAwait(false);
            }

            return Success;
        }

        // The names column fits the longest name (the 2.6 detectors' names are long).
        var width = Math.Max("Inspection".Length, lines.Max(l => l.Info.Name.Length));
        await output.WriteLineAsync($"{"Inspection".PadRight(width)} {"Time",7}  Finding").ConfigureAwait(false);
        foreach (var (info, result) in lines)
        {
            var time = result is null ? string.Empty : result.ElapsedSeconds.ToString("N1", CultureInfo.InvariantCulture) + " s";
            await output.WriteLineAsync($"{info.Name.PadRight(width)} {time,7}  {(result is null ? "unavailable: " + info.Unavailable : SummaryText(result))}").ConfigureAwait(false);
            if (result?.Note is { } note)
            {
                await output.WriteLineAsync($"{string.Empty.PadRight(width)} {string.Empty,7}  Note: {note}").ConfigureAwait(false);
            }
        }

        return Success;
    }

    private static string SummaryText(InspectionResultDto result) => string.Create(CultureInfo.InvariantCulture,
        $"{result.Count:N0} found, {Mb(result.Bytes)}{(result.WastedBytes is { } wasted ? $", {Mb(wasted)} wasted" : string.Empty)}");

    /// <summary>The visible columns of rows, numbers right aligned, widths from the content.</summary>
    private static async Task WriteGridAsync(TextWriter output, IReadOnlyList<ResultColumn> columns, List<IReadOnlyList<string?>> rows)
    {
        var shown = columns.Select((c, i) => (Column: c, Index: i)).Where(c => !c.Column.IsHidden).ToList();
        string Cell(IReadOnlyList<string?> row, (ResultColumn Column, int Index) c) =>
            row[c.Index] is { } text && c.Column.Kind is ResultColumnKind.Integer or ResultColumnKind.Bytes && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number.ToString("N0", CultureInfo.InvariantCulture)
                : OneLine(row[c.Index]);
        var widths = shown.Select(c => Math.Min(80, rows.Select(r => Cell(r, c).Length).Append(c.Column.Name.Length).Max())).ToList();
        bool Right((ResultColumn Column, int Index) c) => c.Column.Kind is ResultColumnKind.Integer or ResultColumnKind.Bytes or ResultColumnKind.Percent;
        await output.WriteLineAsync(string.Join("  ", shown.Select((c, k) => Right(c) ? c.Column.Name.PadLeft(widths[k]) : c.Column.Name.PadRight(widths[k]))).TrimEnd()).ConfigureAwait(false);
        foreach (var row in rows)
        {
            await output.WriteLineAsync(string.Join("  ", shown.Select((c, k) =>
            {
                var text = Cell(row, c);
                return Right(c) ? text.PadLeft(widths[k]) : text.PadRight(widths[k]);
            })).TrimEnd()).ConfigureAwait(false);
        }
    }
}
