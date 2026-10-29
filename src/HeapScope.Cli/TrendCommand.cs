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

/// <summary>The <c>trend</c> command (sprint 3.3): each type's objects and bytes across several dumps, oldest first.</summary>
internal static partial class CliCommands
{
    private static Command BuildTrendCommand(Func<IServiceProvider> services)
    {
        var dumps = new Argument<FileInfo[]>("dumps") { Description = "Two to twenty dumps, oldest first.", Arity = new ArgumentArity(2, TypeTrendColumns.MaxSnapshots) };
        var group = GroupOption();
        var filter = FilterOption();
        var sort = new Option<string>("--sort") { Description = "Sort column, descending: bytes-delta, count-delta, growth-steps or name.", DefaultValueFactory = _ => "bytes-delta" };
        sort.AcceptOnlyFromAmong("bytes-delta", "count-delta", "growth-steps", "name");
        var top = TopOption();
        var all = AllOption();
        var format = FormatOption();
        var command = new Command("trend", "Compare several dumps at once: each type's objects and bytes in every dump (oldest first), the change from the first to the last, and the steps it grew in. Exit codes: 0 ok, 1 error, 64 usage, 130 canceled.");
        command.Arguments.Add(dumps);
        foreach (var option in new Option[] { group, filter, sort, top, all, format })
        {
            command.Options.Add(option);
        }

        AddTopOrAllValidator(command, top, all);
        command.SetAction((parseResult, ct) => RunAsync(parseResult, async (output, error) =>
        {
            var handles = new List<SnapshotHandle>();
            foreach (var file in parseResult.GetValue(dumps)!)
            {
                var handle = await OpenForQueryAsync(file, error, ct).ConfigureAwait(false);
                if (handle is null)
                {
                    return Failure;
                }

                handles.Add(handle);
            }

            var session = services().GetRequiredService<SnapshotSession>();
            var sortName = parseResult.GetValue(sort);
            var trend = await session.QueryTypeTrendAsync(
                handles,
                Enum.Parse<TypeGrouping>(parseResult.GetValue(group)!, ignoreCase: true),
                parseResult.GetValue(filter),
                sortName switch { "count-delta" => TypeTrendColumns.ObjectsDelta, "growth-steps" => TypeTrendColumns.GrowthSteps, "name" => TypeTrendColumns.Name, _ => TypeTrendColumns.BytesDelta },
                descending: sortName != "name",
                null,
                ct).ConfigureAwait(false);
            var result = trend.Result;
            var wanted = parseResult.GetValue(all) ? result.Rows.TotalCount : Math.Min(parseResult.GetValue(top), result.Rows.TotalCount);
            var rows = await FetchRowsAsync(trend.Rows, wanted, ct).ConfigureAwait(false);
            var columns = trend.Rows.Info.Columns;
            switch (parseResult.GetValue(format))
            {
                case "csv":
                    await WriteCsvAsync(output, columns, rows).ConfigureAwait(false);
                    break;
                case "json":
                    await WriteJsonAsync(output, new { Snapshots = handles.Select(h => h.Path), result.TotalBytes, result.UnfilteredRows, result.ExactCounts, Rows = JsonRows(columns, rows) }).ConfigureAwait(false);
                    break;
                default:
                    var n = handles.Count;
                    await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{result.Rows.TotalCount:N0} of {result.UnfilteredRows:N0} rows across {n} dumps; heap bytes {string.Join(" > ", result.TotalBytes.Select(b => b.ToString("N0", CultureInfo.InvariantCulture)))}; showing {rows.Count:N0}")).ConfigureAwait(false);
                    await output.WriteLineAsync(string.Join(" ", Enumerable.Range(1, n).Select(i => $"{"Bytes " + i,14}")) + $" {"Bytes delta",14} {"Grew",5}  Name").ConfigureAwait(false);
                    foreach (var row in rows)
                    {
                        var bytes = Enumerable.Range(1, n).Select(i => $"{long.Parse(row[ColumnAt(columns, TypeTrendColumns.Bytes(i))]!, CultureInfo.InvariantCulture),14:N0}");
                        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                            $"{string.Join(" ", bytes)} {Signed(long.Parse(row[ColumnAt(columns, TypeTrendColumns.BytesDelta)]!, CultureInfo.InvariantCulture)),14} {row[ColumnAt(columns, TypeTrendColumns.GrowthSteps)],5}  {row[0]}")).ConfigureAwait(false);
                    }

                    break;
            }

            await ReleaseQuietlyAsync(trend.Rows, ct).ConfigureAwait(false);
            return Success;
        }));
        return command;
    }
}
