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

namespace HeapScope.Cli;

/// <summary>The <c>open</c> command: load a dump (or reuse its index cache) and print what it holds.</summary>
internal static partial class CliCommands
{
    private static Command BuildOpenCommand(Func<IServiceProvider> services)
    {
        var dump = new Argument<FileInfo>("dump") { Description = "Path of the full memory dump (.dmp)." };
        var rebuild = new Option<bool>("--rebuild") { Description = "Ignore an existing index cache and load the dump again." };
        var mode = new Option<string>("--mode") { Description = "Memory mode: auto, memory or mapped.", DefaultValueFactory = _ => "auto" };
        mode.AcceptOnlyFromAmong("auto", "memory", "mapped");
        var top = new Option<int>("--top") { Description = "Number of types to list.", DefaultValueFactory = _ => 20 };
        var noOvercommit = new Option<bool>("--no-overcommit") { Description = "Fail instead of warning when the load may not fit in available memory." };

        var command = new Command("open", "Open a full dump: load it (or reuse its index cache) and print a summary. Ctrl+C cancels.");
        command.Arguments.Add(dump);
        command.Options.Add(rebuild);
        command.Options.Add(mode);
        command.Options.Add(top);
        command.Options.Add(noOvercommit);
        command.SetAction((parseResult, ct) => RunAsync(parseResult, services, async context =>
        {
            var (output, error) = (context.Output, context.Error);
            var file = parseResult.GetValue(dump)!;
            if (!file.Exists)
            {
                await error.WriteLineAsync($"Error: the file {file.FullName} does not exist.").ConfigureAwait(false);
                return Failure;
            }

            var memoryMode = parseResult.GetValue(mode) switch
            {
                "memory" => SnapshotMemoryMode.InMemory,
                "mapped" => SnapshotMemoryMode.Mapped,
                _ => SnapshotMemoryMode.Auto,
            };
            var handle = await OpenWithProgressAsync(context, file.FullName, ct, parseResult.GetValue(rebuild), memoryMode, failOnWarning: parseResult.GetValue(noOvercommit)).ConfigureAwait(false);
            if (handle is null)
            {
                return Failure;
            }

            var d = handle.Descriptor;
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"""
                Snapshot:     {d.Path}
                Process:      {d.ProcessName ?? "?"} (pid {d.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "?"}), .NET {d.RuntimeVersion ?? "?"}, captured {d.CapturedAtUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "?"}
                Objects:      {d.ObjectCount:N0} ({Mb((long)d.TotalSize)}), {d.TypeCount:N0} types, {d.ReferenceCount:N0} references, {d.RootCount:N0} roots
                Opened:       {d.OpenSeconds:N1} s from {(d.FromCache ? "the index cache" : "the dump")}, {(d.Mode == SnapshotMemoryMode.Mapped ? "mapped" : "in memory")}
                Index cache:  {d.CachePath ?? "not written"}
                """)).ConfigureAwait(false);
            foreach (var note in d.Notes)
            {
                await output.WriteLineAsync("Note: " + note).ConfigureAwait(false);
            }

            // The type summary's defaults: by type, no filter, largest shallow size first.
            var session = context.Get<SnapshotSession>();
            var summary = await session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, null, null, descending: true, null, ct).ConfigureAwait(false);
            var page = await summary.Rows.GetRowsAsync(0, parseResult.GetValue(top), ct).ConfigureAwait(false);
            await WriteTopTypesAsync(output, summary.Rows.Info.Columns, page).ConfigureAwait(false);
            await summary.Rows.ReleaseAsync(ct).ConfigureAwait(false);
            return Success;
        }));
        return command;
    }

    /// <summary>The largest types of a type summary page, as <c>open</c> lists them.</summary>
    internal static async Task WriteTopTypesAsync(TextWriter output, IReadOnlyList<ResultColumn> columns, ResultPage page)
    {
        var cells = new ColumnMap(columns);
        await output.WriteLineAsync($"Top {page.Rows.Count} of {page.TotalCount} types by shallow size:").ConfigureAwait(false);
        foreach (var row in page.Rows)
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"  {cells.Long(row, TypeSummaryColumns.Objects),12:N0} {Mb(cells.Long(row, TypeSummaryColumns.Bytes)),12}  {cells.Text(row, TypeSummaryColumns.Name)}")).ConfigureAwait(false);
        }
    }
}
