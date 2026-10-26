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
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HeapScope.Cli;

/// <summary>
/// The <c>instances</c> and <c>object</c> commands (sprint 1.5): the objects of a type, and one object's fields and
/// references. Values come from the dump; like the other commands they go to the console only, never to the log.
/// </summary>
internal static partial class CliCommands
{
    /// <summary>References printed by <c>object</c> unless <c>--references</c> says otherwise.</summary>
    private const int DefaultReferences = 50;

    private static Command BuildInstancesCommand(Func<IServiceProvider> services)
    {
        var dump = new Argument<FileInfo>("dump") { Description = "Path of the full memory dump (.dmp)." };
        var type = new Option<string>("--type") { Description = "The type's full name, as the summary prints it (System.String, System.Byte[]).", Required = true };
        var module = new Option<string?>("--module")
        {
            Description = "The type's module file name (System.Private.CoreLib.dll); needed only when types in several modules share the name.",
        };
        var filter = new Option<string?>("--filter")
        {
            Description = "Keep objects whose address starts with this (0x1f2a), whose value matches a regular expression (/^Get.*Async$/), or whose value contains this text (any case).",
        };
        var sort = new Option<string>("--sort") { Description = "Sort by bytes, address, generation, value or retained (adds the retained column).", DefaultValueFactory = _ => "bytes" };
        sort.AcceptOnlyFromAmong(s_instanceSortColumns.Keys.ToArray());
        var retained = RetainedOption("Add each object's retained bytes: what it alone keeps alive (computes the snapshot's dominators the first time, then reuses them).");
        var top = TopOption();
        var all = AllOption();
        var format = FormatOption();

        var command = new Command("instances", "List the objects of a type: address, size, generation, and a string's text or an array's length (opens the dump first; reuses its index cache). Exit codes: 0 ok, 1 error, 64 usage, 130 canceled.");
        command.Arguments.Add(dump);
        command.Options.Add(type);
        command.Options.Add(module);
        command.Options.Add(filter);
        command.Options.Add(sort);
        command.Options.Add(retained);
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
            var typeName = parseResult.GetValue(type)!;
            var moduleName = parseResult.GetValue(module) ?? await FindModuleAsync(session, handle, typeName, error, ct).ConfigureAwait(false);
            if (moduleName is null)
            {
                return Failure;
            }

            var sortName = parseResult.GetValue(sort)!;
            var instances = await session.QueryInstancesAsync(
                handle,
                new InstancesQuery(
                    typeName,
                    moduleName,
                    parseResult.GetValue(filter),
                    s_instanceSortColumns[sortName],
                    Descending: sortName is "bytes" or "retained",
                    IncludeRetained: parseResult.GetValue(retained) || sortName == "retained"),
                parseResult.GetValue(retained) || sortName == "retained" ? new StderrProgress(error) : null,
                ct).ConfigureAwait(false);
            var result = instances.Result;
            await WriteNotesAsync(error, "Snapshot", handle).ConfigureAwait(false);
            if (result.MatchedTypes == 0)
            {
                await error.WriteLineAsync($"Error: there is no type {typeName} in module '{moduleName}' in this snapshot.").ConfigureAwait(false);
                await ReleaseQuietlyAsync(instances.Rows, ct).ConfigureAwait(false);
                return Failure;
            }

            if (!result.ValuesAvailable)
            {
                await error.WriteLineAsync("Note: this snapshot cannot read string text, so string values are empty and value filters match nothing.").ConfigureAwait(false);
            }
            else if (result.FilterNote is { } filterNote)
            {
                await error.WriteLineAsync("Note: " + filterNote).ConfigureAwait(false);
            }

            var wanted = parseResult.GetValue(all) ? result.Rows.TotalCount : Math.Min(parseResult.GetValue(top), result.Rows.TotalCount);
            var rows = await FetchRowsAsync(instances.Rows, wanted, ct).ConfigureAwait(false);
            var columns = instances.Rows.Info.Columns;
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
                            result.TotalObjects,
                            result.TotalBytes,
                            MatchingObjects = result.Rows.TotalCount,
                            result.ValuesAvailable,
                            result.Dominators,
                            Rows = JsonRows(columns, rows),
                        }).ConfigureAwait(false);
                    break;
                default:
                    await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"{typeName} ({moduleName}): {result.TotalObjects:N0} objects, {Mb(result.TotalBytes)}; {result.Rows.TotalCount:N0} match, showing {rows.Count:N0}")).ConfigureAwait(false);
                    if (result.Dominators is { } dominators)
                    {
                        await output.WriteLineAsync(RetainedNote(dominators) + " An object no root reaches retains nothing (blank).").ConfigureAwait(false);
                    }

                    var withRetained = result.Dominators is not null;
                    await output.WriteLineAsync($"{"Address",-18} {"Bytes",14} {"Generation",-12}{(withRetained ? $" {"Retained",16}" : string.Empty)}  Value").ConfigureAwait(false);
                    foreach (var row in rows)
                    {
                        var retainedCell = !withRetained ? string.Empty
                            : row[5] is { } bytes ? string.Create(CultureInfo.InvariantCulture, $" {long.Parse(bytes, CultureInfo.InvariantCulture),16:N0}")
                            : $" {string.Empty,16}";
                        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                            $"{row[0],-18} {long.Parse(row[2]!, CultureInfo.InvariantCulture),14:N0} {row[3],-12}{retainedCell}  {OneLine(row[4])}")).ConfigureAwait(false);
                    }

                    break;
            }

            await ReleaseQuietlyAsync(instances.Rows, ct).ConfigureAwait(false);
            return Success;
        }));
        return command;
    }

    private static Command BuildObjectCommand(Func<IServiceProvider> services)
    {
        var dump = new Argument<FileInfo>("dump") { Description = "Path of the full memory dump (.dmp)." };
        var address = new Argument<string>("address") { Description = "The object's address in hex, as 'instances' prints it (0x1f2a3b40)." };
        address.Validators.Add(result =>
        {
            if (!TryParseAddress(result.GetValueOrDefault<string>(), out _))
            {
                result.AddError($"'{result.GetValueOrDefault<string>()}' is not an address: use hex, such as 0x1f2a3b40.");
            }
        });
        var references = new Option<int>("--references") { Description = "Outgoing references to print (0 for none).", DefaultValueFactory = _ => DefaultReferences };
        references.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() < 0)
            {
                result.AddError("--references must be 0 or more.");
            }
        });
        var format = new Option<string>("--format") { Description = "Output: text or json.", DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json");
        var retained = RetainedOption("Add the object's retained bytes and its immediate dominator, the closest object every path from a root to it passes through (computes the snapshot's dominators the first time, then reuses them).");

        var command = new Command("object", "Show one object: type, size, generation, whether a root holds it, its fields (nested) and the objects it references (opens the dump first; reuses its index cache). A snapshot that cannot read fields (a gcdump) prints why on stderr. Exit codes: 0 ok, 1 error (such as no object at the address), 64 usage, 130 canceled.");
        command.Arguments.Add(dump);
        command.Arguments.Add(address);
        command.Options.Add(references);
        command.Options.Add(format);
        command.Options.Add(retained);
        command.SetAction((parseResult, ct) => RunAsync(parseResult, async (output, error) =>
        {
            var handle = await OpenForQueryAsync(parseResult.GetValue(dump)!, error, ct).ConfigureAwait(false);
            if (handle is null)
            {
                return Failure;
            }

            var session = services().GetRequiredService<SnapshotSession>();
            TryParseAddress(parseResult.GetValue(address), out var at);
            var details = await session.GetObjectAsync(handle, at, progress: null, ct).ConfigureAwait(false);
            var retainedResult = parseResult.GetValue(retained)
                ? await session.QueryRetainedAsync(handle, at, new StderrProgress(error), ct).ConfigureAwait(false)
                : null;
            await WriteNotesAsync(error, "Snapshot", handle).ConfigureAwait(false);
            var wanted = parseResult.GetValue(references);
            var referenceRows = new List<IReadOnlyList<string?>>();
            IReadOnlyList<ResultColumn> referenceColumns = [];
            if (wanted > 0 && details.ReferenceCount > 0)
            {
                var set = await session.QueryReferencesAsync(handle, at, progress: null, ct).ConfigureAwait(false);
                referenceRows = await FetchRowsAsync(set, Math.Min(wanted, set.Info.TotalCount), ct).ConfigureAwait(false);
                referenceColumns = set.Info.Columns;
                await ReleaseQuietlyAsync(set, ct).ConfigureAwait(false);
            }

            // A snapshot without field values (a gcdump) still has the object's graph facts: print those and say why
            // the fields are missing. Not an error: the object exists and everything the snapshot knows is printed.
            if (details.FieldsUnavailable is { } why)
            {
                await error.WriteLineAsync("Note: " + why).ConfigureAwait(false);
            }

            if (parseResult.GetValue(format) == "json")
            {
                await WriteJsonAsync(output, new
                    {
                        Address = Hex(details.Address),
                        details.TypeName,
                        details.Bytes,
                        HeapKind = details.HeapKind.ToString(),
                        details.IsRooted,
                        details.ReferenceCount,
                        details.Text,
                        details.FieldsUnavailable,
                        Fields = details.Fields?.Select(JsonField),
                        References = JsonRows(referenceColumns, referenceRows),
                        Retained = retainedResult is null ? null : new
                        {
                            retainedResult.Reachable,
                            retainedResult.RetainedBytes,
                            Dominator = retainedResult.DominatorAddress == 0 ? null : Hex(retainedResult.DominatorAddress),
                            retainedResult.DominatorType,
                            retainedResult.DominatedByRoots,
                            retainedResult.Index,
                        },
                    }).ConfigureAwait(false);
                return Success;
            }

            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"""
                Object:      {Hex(details.Address)}
                Type:        {details.TypeName}
                Size:        {details.Bytes:N0} bytes
                Generation:  {details.HeapKind}
                Rooted:      {(details.IsRooted ? "yes, a root holds it directly" : "no direct root")}
                """)).ConfigureAwait(false);
            if (details.Text is { } text)
            {
                await output.WriteLineAsync("Text:        " + OneLine(text)).ConfigureAwait(false);
            }

            if (retainedResult is { } r)
            {
                await output.WriteLineAsync(r.Reachable
                    ? string.Create(CultureInfo.InvariantCulture, $"Retained:    {r.RetainedBytes:N0} bytes (what it alone keeps alive)")
                    : "Retained:    nothing. " + r.UnreachableNote).ConfigureAwait(false);
                if (r.Index.Note is { } note)
                {
                    await output.WriteLineAsync("Note:        " + note).ConfigureAwait(false);
                }

                if (r.Reachable)
                {
                    await output.WriteLineAsync(r.DominatedByRoots
                        ? "Dominator:   the roots (a root holds it, or several roots reach it on separate paths)"
                        : $"Dominator:   {Hex(r.DominatorAddress)}  {r.DominatorType}").ConfigureAwait(false);
                }
            }

            if (details.Fields is { } fields)
            {
                await output.WriteLineAsync($"Fields ({fields.Count}):").ConfigureAwait(false);
                await WriteFieldsAsync(output, fields, depth: 1).ConfigureAwait(false);
            }

            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"References ({details.ReferenceCount:N0}, showing {referenceRows.Count:N0}):")).ConfigureAwait(false);
            foreach (var row in referenceRows)
            {
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"  {row[1],-18} {long.Parse(row[3]!, CultureInfo.InvariantCulture),12:N0}  {row[2]}{(string.IsNullOrEmpty(row[0]) ? string.Empty : "  via " + row[0])}")).ConfigureAwait(false);
            }

            return Success;
        }));
        return command;
    }

    /// <summary>CLI sort names and the instance columns they sort by.</summary>
    private static readonly Dictionary<string, string> s_instanceSortColumns = new(StringComparer.Ordinal)
    {
        ["bytes"] = InstanceColumns.Bytes,
        ["address"] = InstanceColumns.Address,
        ["generation"] = InstanceColumns.Generation,
        ["value"] = InstanceColumns.Value,
        ["retained"] = InstanceColumns.Retained,
    };

    /// <summary>
    /// The module of the only type with this exact name, or null after printing why not (no such type, or types in
    /// several modules share it and <c>--module</c> must choose).
    /// </summary>
    private static async Task<string?> FindModuleAsync(SnapshotSession session, SnapshotHandle handle, string typeName, TextWriter error, CancellationToken cancellationToken)
    {
        var summary = await session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, typeName, TypeSummaryColumns.Name, descending: false, progress: null, cancellationToken).ConfigureAwait(false);
        var columns = summary.Rows.Info.Columns.Select(c => c.Name).ToList();
        var (nameAt, moduleAt) = (columns.IndexOf(TypeSummaryColumns.TypeName), columns.IndexOf(TypeSummaryColumns.Module));
        var rows = await FetchRowsAsync(summary.Rows, summary.Result.Rows.TotalCount, cancellationToken).ConfigureAwait(false);
        await ReleaseQuietlyAsync(summary.Rows, cancellationToken).ConfigureAwait(false);
        var modules = rows.Where(r => r[nameAt] == typeName).Select(r => r[moduleAt] ?? string.Empty).Distinct(StringComparer.Ordinal).ToList();
        switch (modules.Count)
        {
            case 1:
                return modules[0];
            case 0:
                await error.WriteLineAsync($"Error: there is no type {typeName} in this snapshot. Names are case sensitive and include the namespace; 'summary --filter' finds them.").ConfigureAwait(false);
                return null;
            default:
                await error.WriteLineAsync($"Error: types named {typeName} exist in {modules.Count} modules ({string.Join(", ", modules)}); choose one with --module.").ConfigureAwait(false);
                return null;
        }
    }

    private static async Task WriteFieldsAsync(TextWriter output, IReadOnlyList<ObjectFieldDto> fields, int depth)
    {
        var indent = new string(' ', depth * 2);
        foreach (var field in fields)
        {
            var at = field.Address != 0 && field.Kind is ObjectFieldKindDto.Reference or ObjectFieldKindDto.String or ObjectFieldKindDto.Array
                ? "  " + Hex(field.Address)
                : string.Empty;
            var type = string.IsNullOrEmpty(field.TypeName) ? string.Empty : $" ({field.TypeName})";
            await output.WriteLineAsync($"{indent}{field.Name}{type} = {OneLine(field.Value)}{at}").ConfigureAwait(false);
            await WriteFieldsAsync(output, field.Children, depth + 1).ConfigureAwait(false);
        }
    }

    private static object JsonField(ObjectFieldDto field) => new
    {
        field.Name,
        field.TypeName,
        Kind = field.Kind.ToString(),
        field.Value,
        Address = field.Address == 0 ? null : Hex(field.Address),
        Children = field.Children.Count == 0 ? null : field.Children.Select(JsonField),
    };

    /// <summary>Parses an address in hex, with or without 0x.</summary>
    internal static bool TryParseAddress(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var digits = text.Trim();
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        return digits.Length is > 0 and <= 16 && ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
    }

    private static string Hex(ulong address) => string.Create(CultureInfo.InvariantCulture, $"0x{address:x}");

    /// <summary>A value on one line: line breaks and tabs escaped, so a string cannot break the table.</summary>
    private static string OneLine(string? value) =>
        value is null ? string.Empty : value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);

    /// <summary>Releases a result set after its output is written; a worker gone by then costs nothing printed.</summary>
    private static async Task ReleaseQuietlyAsync(ResultSetHandle rows, CancellationToken cancellationToken)
    {
        try
        {
            await rows.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WorkerCrashedException or WorkerOperationException)
        {
            // Releasing is housekeeping in a process about to exit; the output is out.
        }
    }
}
