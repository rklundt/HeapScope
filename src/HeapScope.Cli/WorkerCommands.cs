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
using HeapScope.Application;
using HeapScope.Application.Presentation;
using HeapScope.Contracts;

namespace HeapScope.Cli;

/// <summary>The <c>worker</c> commands (ping, info) and <c>synthetic</c>, a worker diagnostic.</summary>
internal static partial class CliCommands
{
    private static Command BuildWorkerCommand(Func<IServiceProvider> services)
    {
        var ping = new Command("ping", "Start (or attach to) the analysis worker and measure a round trip.");
        ping.SetAction((parseResult, ct) => RunAsync(parseResult, services, async context =>
        {
            var operations = context.Get<WorkerOperations>();
            var elapsed = await operations.PingAsync(ct).ConfigureAwait(false);
            var info = operations.Worker.Info!;
            await context.Output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Worker {info.ProductVersion} (pid {info.ProcessId}) responded in {elapsed.TotalMilliseconds:N1} ms")).ConfigureAwait(false);
            return Success;
        }));

        var info = new Command("info", "Show the analysis worker's version, process and memory.");
        info.SetAction((parseResult, ct) => RunAsync(parseResult, services, async context =>
        {
            var operations = context.Get<WorkerOperations>();
            var workerInfo = await operations.GetInfoAsync(ct).ConfigureAwait(false);
            await context.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"""
                Version:           {workerInfo.ProductVersion}
                Protocol:          {workerInfo.ProtocolVersion}
                Process id:        {workerInfo.ProcessId}
                Server GC:         {workerInfo.ServerGc}
                Working set:       {Mb(workerInfo.WorkingSetBytes)}
                Managed heap:      {Mb(workerInfo.ManagedHeapBytes)}
                Available memory:  {Mb(workerInfo.TotalAvailableMemoryBytes)}
                """)).ConfigureAwait(false);
            return Success;
        }));

        var worker = new Command("worker", "Commands for the analysis worker process.");
        worker.Subcommands.Add(ping);
        worker.Subcommands.Add(info);
        return worker;
    }

    private static Command BuildSyntheticCommand(Func<IServiceProvider> services)
    {
        var objects = new Option<int>("--objects") { Description = "Number of objects.", DefaultValueFactory = _ => 5_000_000 };
        var refs = new Option<double>("--refs") { Description = "Average references per object.", DefaultValueFactory = _ => 3 };
        var seed = new Option<int>("--seed") { Description = "Random seed.", DefaultValueFactory = _ => 42 };
        var top = new Option<int>("--top") { Description = "Number of types to list.", DefaultValueFactory = _ => 10 };

        var command = new Command("synthetic", "Build a synthetic object graph in the worker (diagnostics). Ctrl+C cancels.");
        command.Options.Add(objects);
        command.Options.Add(refs);
        command.Options.Add(seed);
        command.Options.Add(top);
        command.SetAction((parseResult, ct) => RunAsync(parseResult, services, async context =>
        {
            var output = context.Output;
            var operations = context.Get<WorkerOperations>();
            var request = new SyntheticJobRequest(parseResult.GetValue(objects), parseResult.GetValue(refs), parseResult.GetValue(seed));
            var result = await AwaitJobAsync(context, operations.StartSyntheticJob(request), ct).ConfigureAwait(false);
            await WriteSyntheticAsync(output, result, await operations.GetRowsAsync(result.TypeSummary.Id, 0, parseResult.GetValue(top), ct).ConfigureAwait(false)).ConfigureAwait(false);
            await operations.ReleaseResultSetAsync(result.TypeSummary.Id, ct).ConfigureAwait(false);
            return Success;
        }));
        return command;
    }

    /// <summary>A synthetic graph's totals and its largest types, from a page of its type summary.</summary>
    internal static async Task WriteSyntheticAsync(TextWriter output, SyntheticJobResult result, ResultPage page)
    {
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{result.ObjectCount:N0} objects, {result.ReferenceCount:N0} references, {Mb((long)result.TotalSize)} shallow size, {Mb(result.ColumnBytes)} graph columns, {result.ElapsedSeconds:N2} s")).ConfigureAwait(false);
        var cells = new ColumnMap(result.TypeSummary.Columns);
        await output.WriteLineAsync($"Top {page.Rows.Count} of {page.TotalCount} types by shallow size:").ConfigureAwait(false);
        foreach (var row in page.Rows)
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"  {cells.Text(row, TypeSummaryColumns.Name),-24} {cells.Long(row, TypeSummaryColumns.Objects),12:N0} objects {Mb(cells.Long(row, TypeSummaryColumns.Bytes)),12}")).ConfigureAwait(false);
        }
    }
}
