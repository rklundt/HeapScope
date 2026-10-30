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
using HeapScope.Application.Monitoring;
using HeapScope.Application.Presentation;
using HeapScope.Application.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace HeapScope.Cli;

/// <summary>
/// The <c>monitor</c> command (sprint 1.7, ADR-021): a process's runtime counters every interval, and snapshots taken by
/// rules into the current session. Ctrl+C stops (a capture already running finishes first).
/// </summary>
internal static partial class CliCommands
{
    private static Command BuildMonitorCommand(Func<IServiceProvider> services)
    {
        var pid = new Option<int>("--pid") { Description = "Process id to monitor (see 'list').", Required = true };
        var interval = new Option<int>("--interval") { Description = "Seconds between samples.", DefaultValueFactory = _ => 1 };
        interval.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() < 1)
            {
                r.AddError("--interval must be 1 second or more.");
            }
        });
        var duration = new Option<int?>("--duration") { Description = "Seconds to monitor; default: until Ctrl+C or the process exits." };
        // Each rule option is checked on its own by the parser the app shares (in the CLI's grammar), so each says what is wrong.
        var every = new Option<string?>("--every") { Description = "Take a snapshot every interval: 90s, 5m, 1h." };
        AddRuleValidator(every, text => new MonitorRulesInput(Every: text), parse => parse.EveryError);
        var above = new Option<string?>("--above") { Description = "Take a snapshot when the GC heap exceeds this size: 500MB, 2GB." };
        AddRuleValidator(above, text => new MonitorRulesInput(Above: text), parse => parse.AboveError);
        var growth = new Option<string?>("--growth") { Description = "Take a snapshot when the GC heap grew by this much over the first sample: 100MB or 50%." };
        AddRuleValidator(growth, text => new MonitorRulesInput(Growth: text), parse => parse.GrowthError);
        var rearm = new Option<string>("--rearm") { Description = "After a size rule fired: once (never again), below (again after the heap falls back under it) or step (again for each further step).", DefaultValueFactory = _ => "below" };
        rearm.AcceptOnlyFromAmong("once", "below", "step");
        var max = new Option<int?>("--max") { Description = "Take at most this many snapshots." };
        max.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int?>() is { } count
                && MonitorRulesParser.Parse(new MonitorRulesInput(Max: MaxText(count)), MonitorRuleGrammar.Suffixed).MaxError is { } error)
            {
                r.AddError(error);
            }
        });
        var format = new Option<string>("--format") { Description = "Output: table or csv.", DefaultValueFactory = _ => "table" };
        format.AcceptOnlyFromAmong("table", "csv");
        var kind = KindOption();

        var command = new Command("monitor", "Watch a process's GC heap, collections and allocations every interval, and capture full dumps (or gcdumps, --kind gcdump) into the current session when rules fire. Ctrl+C stops. Exit codes: 0 ok, 1 error, 64 usage, 130 canceled.");
        foreach (var option in new Option[] { pid, interval, duration, every, above, growth, rearm, max, format, kind })
        {
            command.Options.Add(option);
        }

        command.SetAction((parseResult, ct) => RunAsync(parseResult, services, async context =>
        {
            var (output, error) = (context.Output, context.Error);
            // The validators refused anything the parser refuses, so the rules parse here.
            var monitorRules = MonitorRulesParser.Parse(
                new MonitorRulesInput(parseResult.GetValue(every), parseResult.GetValue(above), parseResult.GetValue(growth), parseResult.GetValue(max) is { } count ? MaxText(count) : null, parseResult.GetValue(rearm)),
                MonitorRuleGrammar.Suffixed).Rules!;
            var rules = monitorRules.Rules;
            var csv = parseResult.GetValue(format) == "csv";
            await error.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"Monitoring pid {parseResult.GetValue(pid)} every {parseResult.GetValue(interval)} s{(rules.Count > 0 ? "; rules: " + string.Join(", ", rules.Select(r => r.Label)) : string.Empty)} (Ctrl+C stops)...")).ConfigureAwait(false);
            if (!csv)
            {
                await output.WriteLineAsync($"{"Time",-8} {"GC heap",10} {"After GC",10} {"Gen1",9} {"Gen2",10} {"LOH",9} {"POH",8} {"GCs 0/1/2",10} {"Allocated",10} {"Last GC",7}").ConfigureAwait(false);
            }
            else
            {
                await output.WriteLineAsync("time,gc_heap_bytes,gen0_bytes,gen1_bytes,gen2_bytes,loh_bytes,poh_bytes,gen0_gcs,gen1_gcs,gen2_gcs,allocated_bytes,time_in_gc_percent,working_set_bytes").ConfigureAwait(false);
            }

            var monitor = context.Get<MonitorService>();
            var gate = new object();
            await using var run = monitor.Start(
                parseResult.GetValue(pid),
                new MonitorOptions(TimeSpan.FromSeconds(parseResult.GetValue(interval)), monitorRules, CaptureKind: KindOf(parseResult.GetValue(kind))),
                attached =>
                {
                    attached.SampleReceived += (_, s) =>
                    {
                        lock (gate)
                        {
                            output.WriteLine(csv ? CsvLine(s.Counters) : TableLine(s));
                        }
                    };
                    attached.Captured += (_, c) =>
                    {
                        lock (gate)
                        {
                            error.WriteLine(c.Result is { } result
                                ? string.Create(CultureInfo.InvariantCulture, $"Snapshot ({c.Rule}): {result.Path} ({Mb(result.SizeBytes)})")
                                : $"Snapshot ({c.Rule}) failed: {c.Error}");
                        }
                    };
                });

            try
            {
                var stopAfter = parseResult.GetValue(duration) is { } seconds ? Task.Delay(TimeSpan.FromSeconds(seconds), ct) : Task.Delay(Timeout.Infinite, ct);
                await Task.WhenAny(run.Completion, stopAfter).ConfigureAwait(false);
                run.Stop();
                await run.Completion.ConfigureAwait(false);
            }
            catch (CaptureFailedException ex)
            {
                await error.WriteLineAsync("Error: " + ex.Message).ConfigureAwait(false);
                return Failure;
            }

            if (run.TimelinePath is { } timeline)
            {
                await error.WriteLineAsync("Timeline: " + timeline).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            return Success;
        }));
        return command;
    }

    /// <summary>Refuses a rule option's value with the shared parser's message for that field.</summary>
    private static void AddRuleValidator(Option<string?> option, Func<string, MonitorRulesInput> input, Func<MonitorRulesParse, string?> error) =>
        option.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<string?>() is { } text && error(MonitorRulesParser.Parse(input(text), MonitorRuleGrammar.Suffixed)) is { } message)
            {
                r.AddError(message);
            }
        });

    /// <summary>The <c>--max</c> count as the parser reads it (System.CommandLine has already read the integer).</summary>
    private static string MaxText(int count) => count.ToString(CultureInfo.InvariantCulture);

    private static string TableLine(MonitorSample sample)
    {
        var s = sample.Counters;
        return string.Create(CultureInfo.InvariantCulture,
            $"{s.At.ToLocalTime():HH:mm:ss} {Mb(s.GcHeapBytes),10} {Mb(s.HeapAfterGcBytes),10} {Mb(s.Gen1Bytes),9} {Mb(s.Gen2Bytes),10} {Mb(s.LargeObjectHeapBytes),9} {Mb(s.PinnedObjectHeapBytes),8} {$"{s.Gen0Collections}/{s.Gen1Collections}/{s.Gen2Collections}",10} {Mb(s.AllocatedBytes),10} {s.TimeInGcPercent,6:0.#}%{(sample.HighGcPressure ? "  high GC pressure" : string.Empty)}");
    }

    private static string CsvLine(HeapSample s) => string.Create(CultureInfo.InvariantCulture,
        $"{s.At:O},{s.GcHeapBytes},{s.Gen0Bytes},{s.Gen1Bytes},{s.Gen2Bytes},{s.LargeObjectHeapBytes},{s.PinnedObjectHeapBytes},{s.Gen0Collections},{s.Gen1Collections},{s.Gen2Collections},{s.AllocatedBytes},{s.TimeInGcPercent:0.##},{s.WorkingSetBytes}");
}
