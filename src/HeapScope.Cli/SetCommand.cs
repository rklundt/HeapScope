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
/// The <c>set</c> command (sprint 3.1, ADR-030): an object set (types, a namespace, an assembly, objects by address, an
/// inspection's row, or what any of these retains) and one of its views, as the app shows them.
/// </summary>
internal static partial class CliCommands
{
    private static readonly string[] s_setViews = ["types", "instances", "generations", "dominators", "retained"];

    private static Command BuildSetCommand(Func<IServiceProvider> services)
    {
        var dump = new Argument<FileInfo>("snapshot") { Description = "Path of the full memory dump (.dmp) or gcdump (.gcdump)." };
        var types = new Option<string[]>("--type") { Description = "A type's full name; repeat for several types.", AllowMultipleArgumentsPerToken = true };
        var module = new Option<string?>("--module") { Description = "The types' module file name; needed only when types in several modules share a name." };
        var ns = new Option<string?>("--namespace") { Description = "Every type of a namespace, as the summary's namespace rows name them." };
        var assembly = new Option<string?>("--assembly") { Description = "Every type of an assembly, by its module file name (Leaks.dll)." };
        var addresses = new Option<string[]>("--address") { Description = "An object's address (0x...); repeat for several objects.", AllowMultipleArgumentsPerToken = true };
        var inspection = new Option<string?>("--inspection") { Description = "An inspection's name; with --row, the objects that row counted." };
        var row = new Option<int>("--row") { Description = "The inspection row's number, from 0, in the order 'inspect' lists the rows.", DefaultValueFactory = _ => -1 };
        var baseline = new Option<FileInfo?>("--baseline") { Description = "The earlier dump an inspection that compares against one uses." };
        var retained = new Option<bool>("--retained") { Description = "The objects the set keeps alive together instead of its own members." };
        var view = new Option<string>("--view") { Description = "types, instances, generations, dominators (the members no other member dominates) or retained (what the set keeps alive, by type).", DefaultValueFactory = _ => "types" };
        view.AcceptOnlyFromAmong(s_setViews);
        var top = TopOption();
        var all = AllOption();
        var format = FormatOption();

        var command = new Command("set", "Show a set of objects, chosen by type, namespace, assembly, address or inspection row, in one of the app's set views. Exit codes: 0 ok, 1 error (the set selects nothing), 64 usage, 130 canceled.");
        command.Arguments.Add(dump);
        foreach (var option in new Option[] { types, module, ns, assembly, addresses, inspection, row, baseline, retained, view, top, all, format })
        {
            command.Options.Add(option);
        }

        AddTopOrAllValidator(command, top, all);
        command.Validators.Add(result =>
        {
            var chosen = new[]
            {
                result.GetValue(types) is { Length: > 0 },
                result.GetValue(ns) is not null,
                result.GetValue(assembly) is not null,
                result.GetValue(addresses) is { Length: > 0 },
                result.GetValue(inspection) is not null,
            }.Count(c => c);
            if (chosen != 1)
            {
                result.AddError("Choose the set with exactly one of --type, --namespace, --assembly, --address or --inspection.");
            }
            else if (result.GetValue(inspection) is not null && result.GetValue(row) < 0)
            {
                result.AddError("--inspection needs --row (from 0).");
            }
            else if (result.GetValue(baseline) is not null && result.GetValue(inspection) is null)
            {
                result.AddError("--baseline goes with --inspection: the earlier dump a growth detector compares against.");
            }
        });
        command.SetAction((parseResult, ct) => RunAsync(parseResult, async (output, error) =>
        {
            var handle = await OpenForQueryAsync(parseResult.GetValue(dump)!, error, ct).ConfigureAwait(false);
            if (handle is null)
            {
                return Failure;
            }

            var session = services().GetRequiredService<SnapshotSession>();
            await WriteNotesAsync(error, "Snapshot", handle).ConfigureAwait(false);
            SnapshotHandle? baselineHandle = null;
            if (parseResult.GetValue(baseline) is { } baselineFile)
            {
                baselineHandle = await OpenForQueryAsync(baselineFile, error, ct).ConfigureAwait(false);
                if (baselineHandle is null)
                {
                    return Failure;
                }
            }

            ObjectSetSpec spec;
            if (parseResult.GetValue(types) is { Length: > 0 } names)
            {
                var keys = new List<TypeKeyDto>();
                // Repeat --type for several types: generic names hold commas (the 3.1 review).
                foreach (var name in names)
                {
                    var found = parseResult.GetValue(module) ?? await FindModuleAsync(session, handle, name, error, ct).ConfigureAwait(false);
                    if (found is null)
                    {
                        return Failure;
                    }

                    keys.Add(new TypeKeyDto(name, found));
                }

                spec = ObjectSetSpec.OfTypes(keys);
            }
            else if (parseResult.GetValue(ns) is { } name)
            {
                spec = ObjectSetSpec.OfNamespace(name);
            }
            else if (parseResult.GetValue(assembly) is { } file)
            {
                spec = ObjectSetSpec.OfAssembly(file);
            }
            else if (parseResult.GetValue(addresses) is { Length: > 0 } texts)
            {
                var parsed = new List<ulong>();
                foreach (var text in texts.SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                {
                    var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
                    if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
                    {
                        await error.WriteLineAsync($"Error: {text} is not a hexadecimal address.").ConfigureAwait(false);
                        return Usage;
                    }

                    parsed.Add(address);
                }

                spec = ObjectSetSpec.OfObjects(parsed);
            }
            else
            {
                spec = ObjectSetSpec.OfInspectionRow(parseResult.GetValue(inspection)!, parseResult.GetValue(row));
            }

            if (parseResult.GetValue(retained))
            {
                spec = ObjectSetSpec.RetainedByOf(spec);
            }

            var set = new ObjectSetQuery(spec, baselineHandle);
            var wanted = parseResult.GetValue(all) ? int.MaxValue : parseResult.GetValue(top);
            var fmt = parseResult.GetValue(format)!;
            switch (parseResult.GetValue(view))
            {
                case "instances":
                    var instances = await session.QueryInstancesAsync(handle, new InstancesQuery(string.Empty, null, Set: set), null, ct).ConfigureAwait(false);
                    await WriteSetViewAsync(output, fmt, instances.Rows, wanted, string.Create(CultureInfo.InvariantCulture, $"{instances.Result.TotalObjects:N0} objects of {instances.Result.TotalBytes:N0} bytes, {instances.Result.MatchedTypes:N0} types"), instances.Result, ct).ConfigureAwait(false);
                    break;
                case "dominators":
                    var dominators = await session.QuerySetDominatorsAsync(handle, set, null, ct).ConfigureAwait(false);
                    var d = dominators.Result;
                    await WriteSetViewAsync(output, fmt, dominators.Rows, wanted, RetainedNote(d.Dominators) + Environment.NewLine + string.Create(CultureInfo.InvariantCulture, $"{d.Set.Objects:N0} members: {d.TopMembers:N0} no other member dominates, retaining {d.TopRetained:N0} bytes apart; {d.UnreachableMembers:N0} no root reaches"), d, ct).ConfigureAwait(false);
                    break;
                case "retained":
                    var answer = await session.QueryRetainedBySetAsync(handle, set, null, ct).ConfigureAwait(false);
                    var r = answer.Result;
                    await WriteSetViewAsync(output, fmt, answer.Rows, wanted, RetainedNote(r.Dominators) + Environment.NewLine + string.Create(CultureInfo.InvariantCulture, $"{r.SetObjects:N0} reachable members of {r.SetBytes:N0} bytes retain {r.RetainedObjects:N0} objects of {r.RetainedBytes:N0} bytes exactly"), r, ct).ConfigureAwait(false);
                    break;
                default:
                    var summary = await session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, null, null, descending: true, includeRetained: false, null, ct, set).ConfigureAwait(false);
                    var s = summary.Result;
                    if (parseResult.GetValue(view) == "generations")
                    {
                        await ReleaseQuietlyAsync(summary.Rows, ct).ConfigureAwait(false);
                        if (!s.HasGenerations)
                        {
                            // As every capability gap is said (ARCHITECTURE.md 5.2): the reason, and a failing exit code.
                            await error.WriteLineAsync("Error: " + CapabilityDescriptions.ExplainMissing(SnapshotFeatures.Generations, handle.Descriptor.Kind)).ConfigureAwait(false);
                            return Failure;
                        }

                        await WriteGenerationsAsync(output, fmt, s).ConfigureAwait(false);
                        break;
                    }

                    await WriteSetViewAsync(output, fmt, summary.Rows, wanted, string.Create(CultureInfo.InvariantCulture, $"{s.TotalObjects:N0} objects of {s.TotalBytes:N0} bytes, {s.Rows.TotalCount:N0} types"), s, ct).ConfigureAwait(false);
                    break;
            }

            return Success;
        }));
        return command;
    }

    /// <summary>A set view's rows: a line saying what the set holds, then the shown columns (hidden link columns left out).</summary>
    private static async Task WriteSetViewAsync(TextWriter output, string format, ResultSetHandle handle, int wanted, string heading, object result, CancellationToken cancellationToken)
    {
        var rows = await FetchRowsAsync(handle, Math.Min(wanted, handle.Info.TotalCount), cancellationToken).ConfigureAwait(false);
        var columns = handle.Info.Columns;
        switch (format)
        {
            case "csv":
                await WriteCsvAsync(output, columns, rows).ConfigureAwait(false);
                break;
            case "json":
                await WriteJsonAsync(output, new { Result = result, Rows = JsonRows(columns, rows) }).ConfigureAwait(false);
                break;
            default:
                await output.WriteLineAsync(heading + string.Create(CultureInfo.InvariantCulture, $"; showing {rows.Count:N0}")).ConfigureAwait(false);
                var shown = Enumerable.Range(0, columns.Count).Where(i => !columns[i].IsHidden).ToList();
                await output.WriteLineAsync(string.Join("  ", shown.Select(i => Pad(columns[i], columns[i].Name)))).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    await output.WriteLineAsync(string.Join("  ", shown.Select(i => Pad(columns[i], row[i] ?? string.Empty)))).ConfigureAwait(false);
                }

                break;
        }

        await ReleaseQuietlyAsync(handle, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The set's objects and bytes per heap kind.</summary>
    private static async Task WriteGenerationsAsync(TextWriter output, string format, TypeSummaryResult result)
    {
        if (format == "json")
        {
            await WriteJsonAsync(output, new { result.TotalObjects, result.TotalBytes, result.HasGenerations, result.HeapKinds }).ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync(format == "csv" ? "Heap,Objects,Bytes" : $"{"Heap",-18} {"Objects",14} {"Bytes",18}").ConfigureAwait(false);
        foreach (var kind in result.HeapKinds)
        {
            await output.WriteLineAsync(format == "csv"
                ? string.Create(CultureInfo.InvariantCulture, $"{kind.Kind},{kind.Objects},{kind.Bytes}")
                : string.Create(CultureInfo.InvariantCulture, $"{kind.Kind,-18} {kind.Objects,14:N0} {kind.Bytes,18:N0}")).ConfigureAwait(false);
        }
    }

    /// <summary>Numbers right aligned, text as it is.</summary>
    private static string Pad(ResultColumn column, string text) =>
        column.Kind is ResultColumnKind.Text or ResultColumnKind.Address ? text : text.PadLeft(14);
}
