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
            Description = "The inspection: duplicate-strings, empty-collections, oversized-collections, sparse-arrays, large-arrays, loh, finalizable or fragmentation. Leave out to run all and print a summary.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var top = TopOption();
        var all = AllOption();
        var format = FormatOption();

        var command = new Command("inspect", "Find common memory waste: duplicate strings, empty and oversized collections, sparse and large arrays, the large object heap, finalizable objects, fragmentation (opens the dump first; reuses its index cache). Exit codes: 0 ok, 1 error (an unknown or unavailable inspection), 64 usage, 130 canceled.");
        command.Arguments.Add(dump);
        command.Arguments.Add(name);
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
            var inspections = await session.ListInspectionsAsync(handle, ct).ConfigureAwait(false);
            if (parseResult.GetValue(name) is not { } chosen)
            {
                return await WriteInspectionSummaryAsync(session, handle, inspections, parseResult.GetValue(format)!, output, ct).ConfigureAwait(false);
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

            var run = await session.RunInspectionAsync(handle, inspection.Name, progress: null, ct).ConfigureAwait(false);
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
                    await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(
                        new
                        {
                            inspection.Name,
                            inspection.Title,
                            inspection.Cause,
                            inspection.Fix,
                            result.Count,
                            result.Bytes,
                            result.WastedBytes,
                            result.Note,
                            Rows = JsonRows(columns, rows),
                        },
                        s_json)).ConfigureAwait(false);
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

    private static async Task<int> WriteInspectionSummaryAsync(SnapshotSession session, SnapshotHandle handle, IReadOnlyList<InspectionInfo> inspections, string format, TextWriter output, CancellationToken ct)
    {
        var lines = new List<(InspectionInfo Info, InspectionResultDto? Result)>();
        foreach (var inspection in inspections)
        {
            if (inspection.Unavailable is not null)
            {
                lines.Add((inspection, null));
                continue;
            }

            var run = await session.RunInspectionAsync(handle, inspection.Name, progress: null, ct).ConfigureAwait(false);
            lines.Add((inspection, run.Result));
            await ReleaseQuietlyAsync(run.Rows, ct).ConfigureAwait(false);
        }

        if (format == "json")
        {
            await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(
                lines.Select(l => new
                {
                    l.Info.Name,
                    l.Info.Title,
                    l.Info.Unavailable,
                    l.Result?.Count,
                    l.Result?.Bytes,
                    l.Result?.WastedBytes,
                    l.Result?.Note,
                }),
                s_json)).ConfigureAwait(false);
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

        await output.WriteLineAsync($"{"Inspection",-24} {"Time",7}  Finding").ConfigureAwait(false);
        foreach (var (info, result) in lines)
        {
            var time = result is null ? string.Empty : result.ElapsedSeconds.ToString("N1", CultureInfo.InvariantCulture) + " s";
            await output.WriteLineAsync($"{info.Name,-24} {time,7}  {(result is null ? "unavailable: " + info.Unavailable : SummaryText(result))}").ConfigureAwait(false);
            if (result?.Note is { } note)
            {
                await output.WriteLineAsync($"{string.Empty,-24} {string.Empty,7}  Note: {note}").ConfigureAwait(false);
            }
        }

        return Success;
    }

    private static string SummaryText(InspectionResultDto result) => string.Create(CultureInfo.InvariantCulture,
        $"{result.Count:N0} found, {Mb(result.Bytes)}{(result.WastedBytes is { } wasted ? $", {Mb(wasted)} wasted" : string.Empty)}");

    /// <summary>The visible columns of rows, numbers right aligned, widths from the content.</summary>
    private static async Task WriteGridAsync(TextWriter output, IReadOnlyList<ResultColumn> columns, List<IReadOnlyList<string?>> rows)
    {
        var shown = columns.Select((c, i) => (Column: c, Index: i)).Where(c => !InspectionColumns.IsHidden(c.Column.Name)).ToList();
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
