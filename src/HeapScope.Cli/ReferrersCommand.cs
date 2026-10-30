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
using HeapScope.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HeapScope.Cli;

/// <summary>
/// The <c>referrers</c> command (sprint 2.1): who references one object (each referrer once, with the field holding it on
/// full dumps), or the objects of a type (referrers grouped by their type). The first such query of a snapshot builds its
/// reverse references index and writes it next to the index cache; later runs reuse it.
/// </summary>
internal static partial class CliCommands
{
    /// <summary>CLI sort names and the type referrer columns they sort by.</summary>
    private static readonly Dictionary<string, string> s_typeReferrerSortColumns = new(StringComparer.Ordinal)
    {
        ["references"] = TypeReferrerColumns.References,
        ["objects"] = TypeReferrerColumns.Objects,
        ["targets"] = TypeReferrerColumns.Targets,
        ["bytes"] = TypeReferrerColumns.Bytes,
        ["type"] = TypeReferrerColumns.Type,
    };

    private static Command BuildReferrersCommand(Func<IServiceProvider> services)
    {
        var dump = new Argument<FileInfo>("snapshot") { Description = "Path of the full memory dump (.dmp) or gcdump (.gcdump)." };
        var address = new Option<string?>("--address") { Description = "The object's address in hex (0x1f2a3b40): list the objects referencing it." };
        var type = new Option<string?>("--type") { Description = "A type's full name (System.String): list the types whose objects reference its objects." };
        var module = new Option<string?>("--module")
        {
            Description = "With --type: the type's module file name; needed only when types in several modules share the name.",
        };
        var sort = new Option<string>("--sort") { Description = "With --type: sort by references, objects, targets, bytes or type.", DefaultValueFactory = _ => "references" };
        sort.AcceptOnlyFromAmong(s_typeReferrerSortColumns.Keys.ToArray());
        var top = TopOption();
        var all = AllOption();
        var format = FormatOption();

        var command = new Command("referrers", "List who references an object (--address) or the objects of a type (--type, grouped by the referrers' type). The first query of a snapshot builds its incoming references index and saves it next to the index cache. Exit codes: 0 ok, 1 error, 64 usage, 130 canceled.");
        command.Arguments.Add(dump);
        command.Options.Add(address);
        command.Options.Add(type);
        command.Options.Add(module);
        command.Options.Add(sort);
        command.Options.Add(top);
        command.Options.Add(all);
        command.Options.Add(format);
        AddTopOrAllValidator(command, top, all);
        command.Validators.Add(result =>
        {
            var byAddress = result.GetResult(address) is not null;
            var byType = result.GetResult(type) is not null;
            if (byAddress == byType)
            {
                result.AddError("Give either --address or --type.");
            }
            else if (byAddress && !TryParseAddress(result.GetValue(address), out _))
            {
                result.AddError($"'{result.GetValue(address)}' is not an address: use hex, such as 0x1f2a3b40.");
            }
            else if (byAddress && (result.GetResult(module) is not null || result.GetResult(sort) is { Implicit: false }))
            {
                result.AddError("--module and --sort go with --type.");
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
            var wantAll = parseResult.GetValue(all);
            var wantTop = parseResult.GetValue(top);
            if (parseResult.GetValue(address) is { } text)
            {
                TryParseAddress(text, out var at);
                return await WriteObjectReferrersAsync(session, handle, at, wantAll, wantTop, parseResult.GetValue(format)!, output, error, ct).ConfigureAwait(false);
            }

            var typeName = parseResult.GetValue(type)!;
            var moduleName = parseResult.GetValue(module) ?? await FindModuleAsync(session, handle, typeName, error, ct).ConfigureAwait(false);
            if (moduleName is null)
            {
                return Failure;
            }

            var sortName = parseResult.GetValue(sort)!;
            var list = await session.QueryTypeReferrersAsync(handle, typeName, moduleName, s_typeReferrerSortColumns[sortName], descending: sortName != "type", progress: null, ct).ConfigureAwait(false);
            var result = list.Result;
            if (result.MatchedTypes == 0)
            {
                // Like instances: a mistyped name or module must not pass a script silently (review M8).
                await error.WriteLineAsync($"Error: there is no type {typeName} in module '{moduleName}' in this snapshot.").ConfigureAwait(false);
                await ReleaseQuietlyAsync(list.Rows, ct).ConfigureAwait(false);
                return Failure;
            }

            await WriteIndexNoteAsync(error, result.Index, result.CompleteGraph).ConfigureAwait(false);
            var rows = await FetchRowsAsync(list.Rows, wantAll ? result.Rows.TotalCount : Math.Min(wantTop, result.Rows.TotalCount), ct).ConfigureAwait(false);
            var columns = list.Rows.Info.Columns;
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
                            result.TargetObjects,
                            result.ReferenceCount,
                            ReferrerTypes = result.Rows.TotalCount,
                            result.CompleteGraph,
                            Index = JsonIndex(result.Index),
                            Rows = JsonRows(columns, rows),
                        }).ConfigureAwait(false);
                    break;
                default:
                    await WriteTypeReferrersTableAsync(output, typeName, moduleName, result, columns, rows).ConfigureAwait(false);
                    break;
            }

            await ReleaseQuietlyAsync(list.Rows, ct).ConfigureAwait(false);
            return Success;
        }));
        return command;
    }

    /// <summary>The types whose objects reference a type's objects, as the table prints them.</summary>
    internal static async Task WriteTypeReferrersTableAsync(TextWriter output, string typeName, string moduleName, TypeReferrersResult result, IReadOnlyList<ResultColumn> columns, List<IReadOnlyList<string?>> rows)
    {
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Referrers of {typeName} ({moduleName}): {result.TargetObjects:N0} objects, {result.ReferenceCount:N0} references from {result.Rows.TotalCount:N0} types, showing {rows.Count:N0}")).ConfigureAwait(false);
        await output.WriteLineAsync($"{"Objects",12} {"References",12} {"Targets",12} {"Bytes",14}  Type").ConfigureAwait(false);
        var cells = new ColumnMap(columns);
        foreach (var row in rows)
        {
            var module = cells.Text(row, TypeReferrerColumns.Module);
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{cells.Long(row, TypeReferrerColumns.Objects),12:N0} {cells.Long(row, TypeReferrerColumns.References),12:N0} {cells.Long(row, TypeReferrerColumns.Targets),12:N0} {cells.Long(row, TypeReferrerColumns.Bytes),14:N0}  {cells.Text(row, TypeReferrerColumns.Type)}{(string.IsNullOrEmpty(module) ? string.Empty : $" ({module})")}")).ConfigureAwait(false);
        }
    }

    /// <summary>The objects referencing one object, as the table prints them.</summary>
    internal static async Task WriteObjectReferrersTableAsync(TextWriter output, ulong at, ReferrersResult result, IReadOnlyList<ResultColumn> columns, List<IReadOnlyList<string?>> rows)
    {
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Referrers of {Hex(at)}: {result.Rows.TotalCount:N0} objects, {result.ReferenceCount:N0} references, showing {rows.Count:N0}")).ConfigureAwait(false);
        await output.WriteLineAsync($"{"Address",-18} {"Bytes",12} {"Refs",6}  Type").ConfigureAwait(false);
        var cells = new ColumnMap(columns);
        foreach (var row in rows)
        {
            var field = cells.Text(row, ReferrerColumns.Field);
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{cells.Text(row, ReferrerColumns.Address),-18} {cells.Long(row, ReferrerColumns.Bytes),12:N0} {cells.Long(row, ReferrerColumns.References),6:N0}  {cells.Text(row, ReferrerColumns.Type)}{(string.IsNullOrEmpty(field) ? string.Empty : "  via " + field)}")).ConfigureAwait(false);
        }
    }

    private static async Task<int> WriteObjectReferrersAsync(
        SnapshotSession session,
        SnapshotHandle handle,
        ulong at,
        bool wantAll,
        int wantTop,
        string format,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var list = await session.QueryReferrersAsync(handle, at, progress: null, cancellationToken).ConfigureAwait(false);
        var result = list.Result;
        await WriteIndexNoteAsync(error, result.Index, result.CompleteGraph).ConfigureAwait(false);
        if (result.FieldsNote is { } why)
        {
            await error.WriteLineAsync("Note: " + why).ConfigureAwait(false);
        }

        var rows = await FetchRowsAsync(list.Rows, wantAll ? result.Rows.TotalCount : Math.Min(wantTop, result.Rows.TotalCount), cancellationToken).ConfigureAwait(false);
        var columns = list.Rows.Info.Columns;
        switch (format)
        {
            case "csv":
                await WriteCsvAsync(output, columns, rows).ConfigureAwait(false);
                break;
            case "json":
                await WriteJsonAsync(output, new
                    {
                        Address = Hex(at),
                        Referrers = result.Rows.TotalCount,
                        result.ReferenceCount,
                        result.FieldsNote,
                        result.CompleteGraph,
                        Index = JsonIndex(result.Index),
                        Rows = JsonRows(columns, rows),
                    }).ConfigureAwait(false);
                break;
            default:
                await WriteObjectReferrersTableAsync(output, at, result, columns, rows).ConfigureAwait(false);
                break;
        }

        await ReleaseQuietlyAsync(list.Rows, cancellationToken).ConfigureAwait(false);
        return Success;
    }

    /// <summary>
    /// Says on stderr when the incoming references index was built for this run (the slow first query) and whether it was
    /// saved, and when the snapshot is incomplete, so referrers may be missing (review M9).
    /// </summary>
    private static async Task WriteIndexNoteAsync(TextWriter error, ReverseIndexInfo? index, bool completeGraph)
    {
        if (!completeGraph)
        {
            await error.WriteLineAsync("Note: this snapshot is incomplete (sampled or truncated), so some referrers may be missing.").ConfigureAwait(false);
        }

        if (index is { FromSidecar: false, Seconds: >= 0.5 })
        {
            var seconds = index.Seconds.ToString("N1", CultureInfo.InvariantCulture);
            await error.WriteLineAsync(index.Saved
                ? $"Note: built the incoming references index in {seconds} s and saved it next to the index cache; later runs reuse it."
                : $"Note: built the incoming references index in {seconds} s; it could not be saved, so later runs build it again.").ConfigureAwait(false);
        }
    }

    private static object? JsonIndex(ReverseIndexInfo? index) => index is null ? null : new
    {
        index.FromSidecar,
        Mode = index.Mode.ToString(),
        index.Seconds,
        index.Bytes,
        index.Saved,
    };

    private static long Long(string? cell) => long.Parse(cell!, CultureInfo.InvariantCulture);
}
