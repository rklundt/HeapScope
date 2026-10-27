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
using HeapScope.Application.Allocations;
using HeapScope.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HeapScope.Cli;

/// <summary>
/// The <c>allocations</c> command (sprint 2.5): the types and the call stacks that allocated the most in an interval of
/// an allocations trace (<c>trace --profile allocations</c>). Sizes are estimated from samples.
/// </summary>
internal static partial class CliCommands
{
    private static Command BuildAllocationsCommand(Func<IServiceProvider> services)
    {
        var trace = new Argument<FileInfo>("trace") { Description = "Path of an allocations trace (.nettrace), from 'trace --profile allocations' or HeapScope's Record allocations." };
        var from = new Option<double>("--from") { Description = "Start of the interval, in seconds since the trace started.", DefaultValueFactory = _ => 0 };
        var to = new Option<double?>("--to") { Description = "End of the interval, in seconds (default: the end of the trace)." };
        var type = new Option<string?>("--type") { Description = "Only allocations of this type (its full name, as the types table prints it)." };
        var types = new Option<int>("--types") { Description = "Types to list (0 to 1,000).", DefaultValueFactory = _ => 10 };
        var stacks = new Option<int>("--stacks") { Description = "Call stacks to list (1 to 1,000).", DefaultValueFactory = _ => 10 };
        var frames = new Option<int>("--frames") { Description = "Frames printed per stack in the table, the allocating method first (1 to 100).", DefaultValueFactory = _ => 12 };
        var view = new Option<string>("--view") { Description = "What to print: summary (types and stacks), types or stacks. CSV needs types or stacks.", DefaultValueFactory = _ => "summary" };
        view.AcceptOnlyFromAmong("summary", "types", "stacks");
        var format = FormatOption();
        from.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<double>() < 0)
            {
                result.AddError("--from must be 0 or more.");
            }
        });
        types.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() is < 0 or > 1_000)
            {
                result.AddError("--types must be between 0 and 1,000.");
            }
        });
        stacks.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() is < 1 or > AllocationTreeLimits.MaxStacks)
            {
                result.AddError(string.Create(CultureInfo.InvariantCulture, $"--stacks must be between 1 and {AllocationTreeLimits.MaxStacks:N0}."));
            }
        });
        frames.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() is < 1 or > 100)
            {
                result.AddError("--frames must be between 1 and 100.");
            }
        });

        var command = new Command("allocations", "Show what allocated the most while an allocations trace recorded: the top types and the top call stacks of an interval. Sizes are estimates from samples (about one per 100 KB). Exit codes: 0 ok, 1 error, 64 usage, 130 canceled.");
        command.Arguments.Add(trace);
        command.Options.Add(from);
        command.Options.Add(to);
        command.Options.Add(type);
        command.Options.Add(types);
        command.Options.Add(stacks);
        command.Options.Add(frames);
        command.Options.Add(view);
        command.Options.Add(format);
        command.Validators.Add(result =>
        {
            if (result.GetValue(to) is { } end && end <= result.GetValue(from))
            {
                result.AddError("--to must be after --from.");
            }

            // An option left at its default has no value in a validator.
            if (result.GetValue(format) == "csv" && (result.GetValue(view) ?? "summary") == "summary")
            {
                result.AddError("CSV holds one table: add --view types or --view stacks.");
            }
        });
        command.SetAction((parseResult, ct) => RunAsync(parseResult, async (output, error) =>
        {
            var file = parseResult.GetValue(trace)!;
            if (!file.Exists)
            {
                await error.WriteLineAsync($"Error: {file.FullName} does not exist.").ConfigureAwait(false);
                return Failure;
            }

            var session = services().GetRequiredService<AllocationSession>();
            var handle = await session.StartOpen(file.FullName).Result.ConfigureAwait(false);
            var descriptor = handle.Descriptor;
            var scope = new AllocationScope(parseResult.GetValue(from), parseResult.GetValue(to), parseResult.GetValue(type));
            var shownView = parseResult.GetValue(view)!;
            var wantedTypes = shownView == "stacks" ? 0 : parseResult.GetValue(types);

            List<IReadOnlyList<string?>> typeRows = [];
            IReadOnlyList<ResultColumn> typeColumns = [];
            if (wantedTypes > 0)
            {
                var answer = await session.QueryTypesAsync(handle, scope, ct).ConfigureAwait(false);
                typeColumns = answer.Rows.Info.Columns;
                typeRows = await FetchRowsAsync(answer.Rows, Math.Min(wantedTypes, answer.Result.Types.TotalCount), ct).ConfigureAwait(false);
                await ReleaseQuietlyAsync(answer.Rows, ct).ConfigureAwait(false);
            }

            var top = shownView == "types" ? null : await session.QueryStacksAsync(handle, scope, parseResult.GetValue(stacks), ct).ConfigureAwait(false);
            var totals = await session.QueryStacksAsync(handle, scope with { TypeName = null }, 1, ct).ConfigureAwait(false);
            string Share(long bytes) => totals.SampledBytes == 0 ? "0" : (100.0 * bytes / totals.SampledBytes).ToString("0.##", CultureInfo.InvariantCulture);
            var interval = string.Create(CultureInfo.InvariantCulture, $"{scope.FromSeconds:0.###} s to {(scope.ToSeconds is { } end ? end.ToString("0.###", CultureInfo.InvariantCulture) + " s" : "the end")}");

            switch (parseResult.GetValue(format))
            {
                case "csv" when shownView == "types":
                    await WriteCsvAsync(output, typeColumns, typeRows).ConfigureAwait(false);
                    break;
                case "csv":
                    await output.WriteLineAsync("Rank,Bytes,Samples,Percent,Frames").ConfigureAwait(false);
                    foreach (var (stack, rank) in top!.Stacks.Select((s, i) => (s, i + 1)))
                    {
                        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                            $"{rank},{stack.Bytes},{stack.Samples},{Share(stack.Bytes)},\"{string.Join(" < ", stack.Frames).Replace("\"", "\"\"", StringComparison.Ordinal)}\"")).ConfigureAwait(false);
                    }

                    break;
                case "json":
                    await WriteJsonAsync(output, new
                    {
                        Trace = descriptor.Path,
                        descriptor.ProcessId,
                        descriptor.ProcessName,
                        descriptor.DurationSeconds,
                        From = scope.FromSeconds,
                        To = scope.ToSeconds,
                        Type = scope.TypeName,
                        SampledBytes = top?.SampledBytes ?? totals.SampledBytes,
                        Samples = top?.Samples ?? totals.Samples,
                        BytesAre = ValueBound.Estimate.ToString(),
                        descriptor.LostEvents,
                        descriptor.SamplesWithoutStack,
                        Types = JsonRows(typeColumns, typeRows),
                        Stacks = top?.Stacks.Select(s => new { s.Bytes, s.Samples, Percent = double.Parse(Share(s.Bytes), CultureInfo.InvariantCulture), s.Frames }),
                    }).ConfigureAwait(false);
                    break;
                default:
                    await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"{descriptor.Path}: {descriptor.ProcessName ?? "process"} ({descriptor.ProcessId}), {descriptor.DurationSeconds:N1} s recorded, {descriptor.Samples:N0} samples")).ConfigureAwait(false);
                    await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"From {interval}{(scope.TypeName is null ? string.Empty : ", type " + scope.TypeName)}: about {(top?.SampledBytes ?? totals.SampledBytes):N0} bytes allocated ({(top?.Samples ?? totals.Samples):N0} samples)")).ConfigureAwait(false);
                    if (typeRows.Count > 0)
                    {
                        await output.WriteLineAsync().ConfigureAwait(false);
                        await output.WriteLineAsync($"{"Bytes",16} {"%",7} {"Samples",9}  Type").ConfigureAwait(false);
                        foreach (var row in typeRows)
                        {
                            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                                $"{long.Parse(row[1]!, CultureInfo.InvariantCulture),16:N0} {row[3],7} {long.Parse(row[2]!, CultureInfo.InvariantCulture),9:N0}  {row[0]}")).ConfigureAwait(false);
                        }
                    }

                    if (top is not null)
                    {
                        var perStack = parseResult.GetValue(frames);
                        foreach (var (stack, rank) in top.Stacks.Select((s, i) => (s, i + 1)))
                        {
                            await output.WriteLineAsync().ConfigureAwait(false);
                            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                                $"#{rank}: {stack.Bytes:N0} bytes ({Share(stack.Bytes)} %, {stack.Samples:N0} samples)")).ConfigureAwait(false);
                            foreach (var frame in stack.Frames.Take(perStack))
                            {
                                await output.WriteLineAsync("    " + frame).ConfigureAwait(false);
                            }

                            if (stack.Frames.Count > perStack)
                            {
                                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"    ({stack.Frames.Count - perStack:N0} more frames)")).ConfigureAwait(false);
                            }
                        }
                    }

                    break;
            }

            // Every format: what the sizes mean, and when the trace undercounts.
            await error.WriteLineAsync("Note: " + AllocationTexts.Sampled).ConfigureAwait(false);
            if (descriptor.LostEvents > 0)
            {
                await error.WriteLineAsync("Note: " + AllocationTexts.Lost(descriptor.LostEvents)).ConfigureAwait(false);
            }

            return Success;
        }));
        return command;
    }
}
