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
using System.Diagnostics;
using System.Globalization;
using HeapScope.Application.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace HeapScope.Cli;

/// <summary>The <c>trace</c> command (sprint 1.5): an EventPipe trace of a .NET process, without dotnet-trace.</summary>
internal static partial class CliCommands
{
    private static Command BuildTraceCommand(Func<IServiceProvider> services)
    {
        var pid = new Option<int>("--pid") { Description = "Process id to trace (see 'list').", Required = true };
        var duration = new Option<int>("--duration") { Description = "Seconds to record (Ctrl+C stops earlier and keeps the trace).", DefaultValueFactory = _ => 30 };
        duration.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() < 1)
            {
                result.AddError("--duration must be 1 second or more.");
            }
        });
        var profile = new Option<string>("--profile")
        {
            Description = "cpu: CPU samples with call stacks. gc: collections, pauses and sampled allocations. allocations: sampled allocations with call stacks (read it with 'allocations').",
            DefaultValueFactory = _ => "cpu",
        };
        profile.AcceptOnlyFromAmong("cpu", "gc", "allocations");
        var output = new Option<FileInfo?>("--out") { Description = "The .nettrace file to write; default: <process>-<pid>-<time>.nettrace in the current folder." };

        var command = new Command("trace", "Record an EventPipe trace of a running .NET process (read it with PerfView or Visual Studio). Ctrl+C stops the recording and keeps it.");
        command.Options.Add(pid);
        command.Options.Add(duration);
        command.Options.Add(profile);
        command.Options.Add(output);
        command.SetAction((parseResult, ct) => RunAsync(parseResult, services, async context =>
        {
            var (stdout, error) = (context.Output, context.Error);
            var processId = parseResult.GetValue(pid);
            var path = parseResult.GetValue(output)?.FullName ?? DefaultTracePath(processId);
            var kind = parseResult.GetValue(profile) switch
            {
                "gc" => TraceKind.Gc,
                "allocations" => TraceKind.Allocations,
                _ => TraceKind.Cpu,
            };
            await error.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Recording {kind} trace of {processId} for {parseResult.GetValue(duration)} s (Ctrl+C stops)...")).ConfigureAwait(false);
            TraceResult result;
            try
            {
                result = await context.Get<TraceService>()
                    .RecordAsync(processId, path, kind, TimeSpan.FromSeconds(parseResult.GetValue(duration)), ct)
                    .ConfigureAwait(false);
            }
            catch (CaptureFailedException ex)
            {
                await error.WriteLineAsync("Error: " + ex.Message).ConfigureAwait(false);
                return Failure;
            }

            await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"Trace written: {result.Path} ({Mb(result.Bytes)}, {result.Duration.TotalSeconds:N1} s{(result.StoppedEarly ? ", stopped early" : string.Empty)})")).ConfigureAwait(false);
            await error.WriteLineAsync("Note: a trace holds method and type names and event data such as exception messages, not heap contents. Keep it private.").ConfigureAwait(false);
            return Success;
        }));
        return command;
    }

    private static string DefaultTracePath(int processId)
    {
        string name;
        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            name = "process";
        }

        return Path.GetFullPath(string.Create(CultureInfo.InvariantCulture, $"{name}-{processId}-{DateTime.Now:yyyyMMdd-HHmmss}.nettrace"));
    }
}
