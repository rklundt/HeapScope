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
/// The <c>retainers</c> and <c>retained-by</c> commands (sprint 2.4): the dominator tree from its top (the biggest
/// retainers) or below an object, expanded to a depth; and what every object of a type keeps alive together, exactly.
/// The first query of a snapshot computes its dominators and saves them next to the index cache.
/// </summary>
internal static partial class CliCommands
{
    private static readonly Dictionary<string, DominatorGroupingDto> s_groupings = new(StringComparer.Ordinal)
    {
        ["none"] = DominatorGroupingDto.None,
        ["type"] = DominatorGroupingDto.Type,
        ["namespace"] = DominatorGroupingDto.Namespace,
    };

    private static Command BuildRetainersCommand(Func<IServiceProvider> services)
    {
        var dump = new Argument<FileInfo>("snapshot") { Description = "Path of the full memory dump (.dmp) or gcdump (.gcdump)." };
        var address = new Option<string?>("--address") { Description = "Show the tree below this object (hex, 0x1f2a3b40) instead of the biggest retainers." };
        var group = new Option<string>("--group") { Description = "Children as objects (none) or grouped by type or namespace.", DefaultValueFactory = _ => "none" };
        group.AcceptOnlyFromAmong([.. s_groupings.Keys]);
        var depth = new Option<int>("--depth") { Description = "Levels to expand below the top (0 to 5).", DefaultValueFactory = _ => 0 };
        depth.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() is < 0 or > 5)
            {
                result.AddError("--depth must be between 0 and 5.");
            }
        });
        var top = new Option<int>("--top") { Description = "Rows per level: the biggest children of each expanded row (1 to 1,000).", DefaultValueFactory = _ => 20 };
        top.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() is < DominatorTreeLimits.MinChildren or > DominatorTreeLimits.MaxChildren)
            {
                result.AddError(string.Create(CultureInfo.InvariantCulture, $"--top must be between {DominatorTreeLimits.MinChildren} and {DominatorTreeLimits.MaxChildren:N0}."));
            }
        });
        var format = FormatOption();

        var command = new Command("retainers", "Show the biggest retainers: the objects that alone keep the most memory alive, as the top of the dominator tree, expanded --depth levels (or the tree below --address). The first query of a snapshot computes its dominators and saves them next to the index cache. Exit codes: 0 ok, 1 error, 64 usage, 130 canceled.");
        command.Arguments.Add(dump);
        command.Options.Add(address);
        command.Options.Add(group);
        command.Options.Add(depth);
        command.Options.Add(top);
        command.Options.Add(format);
        command.Validators.Add(result =>
        {
            if (result.GetResult(address) is not null && !TryParseAddress(result.GetValue(address), out _))
            {
                result.AddError($"'{result.GetValue(address)}' is not an address: use hex, such as 0x1f2a3b40.");
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
            await WriteNotesAsync(error, "Snapshot", handle).ConfigureAwait(false);
            TryParseAddress(parseResult.GetValue(address), out var focus);
            var grouping = s_groupings[parseResult.GetValue(group)!];
            var perLevel = parseResult.GetValue(top);

            // Each level expands the rows of the one above it (the tree is flattened by the worker; keys survive re-runs). It
            // stops when the tree is cut or the open keys would pass the worker's limit, and says so (the 2.4 review).
            var expanded = new List<string>();
            DominatorTreeAnswer answer;
            List<IReadOnlyList<string?>> rows;
            var level = 0;
            var wanted = parseResult.GetValue(depth);
            while (true)
            {
                answer = await session.QueryDominatorTreeAsync(handle, expanded, grouping, focus, perLevel, revealKey: null, progress: null, ct).ConfigureAwait(false);
                rows = await FetchRowsAsync(answer.Rows, answer.Result.Rows.TotalCount, ct).ConfigureAwait(false);
                var deepest = focus != 0 ? level + 1 : level;
                var next = level < wanted && !answer.Result.Truncated
                    ? Cells(answer, rows).Where(r => r.Depth == deepest && r.Children > 0 && r.Kind != DominatorRowKinds.More).Select(r => r.Key).ToList()
                    : [];
                if (next.Count == 0)
                {
                    break;
                }

                if (expanded.Count + next.Count > DominatorTreeLimits.MaxExpanded)
                {
                    await error.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"Note: shown to depth {level} of {wanted}: going deeper would open more than {DominatorTreeLimits.MaxExpanded:N0} rows; use a smaller --top or --address.")).ConfigureAwait(false);
                    break;
                }

                await ReleaseQuietlyAsync(answer.Rows, ct).ConfigureAwait(false);
                expanded.AddRange(next);
                level++;
            }

            var result = answer.Result;
            var columns = answer.Rows.Info.Columns;
            switch (parseResult.GetValue(format))
            {
                case "csv":
                    await WriteCsvAsync(output, columns, rows).ConfigureAwait(false);
                    break;
                case "json":
                    await WriteJsonAsync(output, new
                        {
                            Focus = focus == 0 ? null : Hex(focus),
                            Ancestors = result.Ancestors.Select(a => new { Address = Hex(a.Address), Type = a.Name, a.Retained }),
                            result.FocusRetained,
                            result.TotalRetained,
                            result.TopLevel,
                            result.Truncated,
                            result.AncestorDepth,
                            Grouping = grouping.ToString(),
                            Depth = level,
                            Top = perLevel,
                            RetainedBound = Bound(columns, DominatorTreeColumns.Retained).ToString(),
                            result.Dominators,
                            Rows = JsonRows(columns, rows),
                        }).ConfigureAwait(false);
                    break;
                default:
                    await output.WriteLineAsync(RetainedNote(result.Dominators) + " Retained per row: " + ValueBoundTexts.For(Bound(columns, DominatorTreeColumns.Retained))).ConfigureAwait(false);
                    if (focus != 0)
                    {
                        var trail = result.Ancestors.Select(a => $"{a.Name} {Hex(a.Address)}").ToList();
                        if (result.AncestorDepth > result.Ancestors.Count)
                        {
                            trail.Insert(Math.Min(DominatorTreeLimits.TrailHead, trail.Count), string.Create(CultureInfo.InvariantCulture, $"({result.AncestorDepth - result.Ancestors.Count:N0} more)"));
                        }

                        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                            $"{Hex(focus)} retains {result.FocusRetained:N0} bytes of {result.TotalRetained:N0} reachable; dominated by {(trail.Count == 0 ? "the roots only" : string.Join(" > ", trail))}")).ConfigureAwait(false);
                    }
                    else
                    {
                        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                            $"Biggest retainers: {result.TopLevel:N0} top-level of {result.TotalRetained:N0} reachable bytes")).ConfigureAwait(false);
                    }

                    await output.WriteLineAsync($"{"Retained",16} {"%",7} {"Objects",12} {"Address",-18}  Name").ConfigureAwait(false);
                    foreach (var row in Cells(answer, rows))
                    {
                        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                            $"{row.Retained,16:N0} {row.Percent,7} {row.Objects,12:N0} {row.Address,-18}  {new string(' ', 2 * row.Depth)}{row.Name}{(row.Kind == DominatorRowKinds.Group && row.Module is { Length: > 0 } groupModule ? $" [{groupModule}]" : string.Empty)}")).ConfigureAwait(false);
                    }

                    break;
            }

            // Every format: a cut tree must not pass for a whole one (the 2.4 review).
            if (result.Truncated)
            {
                await error.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"Note: the tree was cut at {DominatorTreeLimits.MaxRows:N0} rows; use a smaller --depth or --top, or --address to start lower.")).ConfigureAwait(false);
            }

            await ReleaseQuietlyAsync(answer.Rows, ct).ConfigureAwait(false);
            return Success;
        }));
        return command;
    }

    private static Command BuildRetainedByCommand(Func<IServiceProvider> services)
    {
        var dump = new Argument<FileInfo>("snapshot") { Description = "Path of the full memory dump (.dmp) or gcdump (.gcdump)." };
        var type = new Argument<string>("type") { Description = "A type's full name (System.String)." };
        var module = new Option<string?>("--module") { Description = "The type's module file name; needed only when types in several modules share the name." };
        var top = TopOption();
        var all = AllOption();
        var format = FormatOption();

        var command = new Command("retained-by", "Show what every object of a type keeps alive together, exactly, by type, next to the summary's per-type lower bound (which leaves out what several of them hold together). Exit codes: 0 ok, 1 error (no such type), 64 usage, 130 canceled.");
        command.Arguments.Add(dump);
        command.Arguments.Add(type);
        command.Options.Add(module);
        command.Options.Add(top);
        command.Options.Add(all);
        command.Options.Add(format);
        AddTopOrAllValidator(command, top, all);
        command.SetAction((parseResult, ct) => RunAsync(parseResult, services, async context =>
        {
            var (output, error) = (context.Output, context.Error);
            var handle = await OpenForQueryAsync(context, parseResult.GetValue(dump)!, ct).ConfigureAwait(false);
            if (handle is null)
            {
                return Failure;
            }

            var session = context.Get<SnapshotSession>();
            await WriteNotesAsync(error, "Snapshot", handle).ConfigureAwait(false);
            var typeName = parseResult.GetValue(type)!;
            var moduleName = parseResult.GetValue(module) ?? await FindModuleAsync(session, handle, typeName, error, ct).ConfigureAwait(false);
            if (moduleName is null)
            {
                return Failure;
            }

            // A type that matches nothing is refused by the worker before it computes anything.
            var answer = await session.QueryRetainedBySetAsync(handle, typeName, moduleName, progress: null, ct).ConfigureAwait(false);
            var result = answer.Result;

            var rows = await FetchRowsAsync(answer.Rows, parseResult.GetValue(all) ? result.Types.TotalCount : Math.Min(parseResult.GetValue(top), result.Types.TotalCount), ct).ConfigureAwait(false);
            var columns = answer.Rows.Info.Columns;
            switch (parseResult.GetValue(format))
            {
                case "csv":
                    await WriteCsvAsync(output, columns, rows).ConfigureAwait(false);
                    break;
                case "json":
                    await WriteJsonAsync(output, new
                        {
                            TypeName = typeName,
                            Module = moduleName,
                            result.MatchedTypes,
                            result.AllObjects,
                            result.SetObjects,
                            result.SetBytes,
                            result.RetainedObjects,
                            result.RetainedBytes,
                            result.LowerBoundBytes,
                            result.TotalRetained,
                            result.Seconds,
                            result.Dominators,
                            Rows = JsonRows(columns, rows),
                        }).ConfigureAwait(false);
                    break;
                default:
                    await output.WriteLineAsync(RetainedNote(result.Dominators)).ConfigureAwait(false);
                    await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"{result.SetObjects:N0} reachable objects of {typeName} ({moduleName}), {result.SetBytes:N0} bytes{(result.AllObjects > result.SetObjects ? $" ({result.AllObjects - result.SetObjects:N0} more that no root reaches retain nothing)" : string.Empty)}, retain {result.RetainedObjects:N0} objects of {result.RetainedBytes:N0} bytes exactly; the summary's lower bound is {result.LowerBoundBytes:N0}. {result.Types.TotalCount:N0} types, showing {rows.Count:N0}")).ConfigureAwait(false);
                    await output.WriteLineAsync($"{"Objects",12} {"Retained",16} {"%",7}  Type").ConfigureAwait(false);
                    var (name, objects, retained, percent) = (At(columns, RetainedBySetColumns.Name), At(columns, RetainedBySetColumns.Objects), At(columns, RetainedBySetColumns.Retained), At(columns, RetainedBySetColumns.Percent));
                    foreach (var row in rows)
                    {
                        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                            $"{Long(row[objects]),12:N0} {Long(row[retained]),16:N0} {row[percent],7}  {row[name]}")).ConfigureAwait(false);
                    }

                    break;
            }

            await ReleaseQuietlyAsync(answer.Rows, ct).ConfigureAwait(false);
            return Success;
        }));
        return command;
    }

    /// <summary>A column's index by name (cells are read by name, the 2.4 review).</summary>
    private static int At(IReadOnlyList<ResultColumn> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Name == name)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"The worker's result has no {name} column. The worker and the CLI are from different builds.");
    }

    /// <summary>Whether a column's values are exact or lower bounds, as the worker says (2.3.7 review M7).</summary>
    private static ValueBound Bound(IReadOnlyList<ResultColumn> columns, string name) => columns[At(columns, name)].Bound;

    /// <summary>The tree's rows by column name.</summary>
    private static IEnumerable<TreeCells> Cells(DominatorTreeAnswer answer, List<IReadOnlyList<string?>> rows)
    {
        var at = answer.Rows.Info.Columns.Select((c, i) => (c.Name, i)).ToDictionary(c => c.Name, c => c.i, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            yield return new TreeCells(
                row[at[DominatorTreeColumns.Key]]!,
                int.Parse(row[at[DominatorTreeColumns.Depth]]!, CultureInfo.InvariantCulture),
                row[at[DominatorTreeColumns.Kind]]!,
                row[at[DominatorTreeColumns.Name]]!,
                row[at[DominatorTreeColumns.Module]],
                row[at[DominatorTreeColumns.Address]] ?? string.Empty,
                Long(row[at[DominatorTreeColumns.Objects]]),
                Long(row[at[DominatorTreeColumns.Retained]]),
                row[at[DominatorTreeColumns.Percent]]!,
                int.Parse(row[at[DominatorTreeColumns.Children]]!, CultureInfo.InvariantCulture));
        }
    }

    private sealed record TreeCells(string Key, int Depth, string Kind, string Name, string? Module, string Address, long Objects, long Retained, string Percent, int Children);
}
