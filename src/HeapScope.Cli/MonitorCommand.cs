using System.CommandLine;
using System.Globalization;
using HeapScope.Application.Monitoring;
using HeapScope.Application.Sessions;
using HeapScope.Application.Snapshots;
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
        var every = new Option<string?>("--every") { Description = "Take a snapshot every interval: 90s, 5m, 1h." };
        every.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<string?>() is { } text && !TryParseInterval(text, out _))
            {
                r.AddError($"--every: '{text}' is not an interval such as 90s, 5m or 1h.");
            }
        });
        var above = new Option<string?>("--above") { Description = "Take a snapshot when the GC heap exceeds this size: 500MB, 2GB." };
        var growth = new Option<string?>("--growth") { Description = "Take a snapshot when the GC heap grew by this much over the first sample: 100MB or 50%." };
        foreach (var option in new[] { above, growth })
        {
            option.Validators.Add(r =>
            {
                if (r.GetValueOrDefault<string?>() is { } text
                    && (!GrowthThreshold.TryParse(text, out var threshold) || (option == above && threshold.Percent is not null) || threshold.Bytes is 0 || threshold.Percent is 0))
                {
                    r.AddError($"{option.Name}: '{text}' is not a size above zero such as 500MB{(option == growth ? " or a percent such as 50%" : string.Empty)}.");
                }
            });
        }

        var rearm = new Option<string>("--rearm") { Description = "After a size rule fired: once (never again), below (again after the heap falls back under it) or step (again for each further step).", DefaultValueFactory = _ => "below" };
        rearm.AcceptOnlyFromAmong("once", "below", "step");
        var max = new Option<int?>("--max") { Description = "Take at most this many snapshots." };
        max.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int?>() is < 1)
            {
                r.AddError("--max must be 1 or more.");
            }
        });
        var format = new Option<string>("--format") { Description = "Output: table or csv.", DefaultValueFactory = _ => "table" };
        format.AcceptOnlyFromAmong("table", "csv");

        var command = new Command("monitor", "Watch a process's GC heap, collections and allocations every interval, and capture full dumps into the current session when rules fire. Ctrl+C stops. Exit codes: 0 ok, 1 error, 64 usage, 130 canceled.");
        foreach (var option in new Option[] { pid, interval, duration, every, above, growth, rearm, max, format })
        {
            command.Options.Add(option);
        }

        command.SetAction((parseResult, ct) => RunAsync(parseResult, async (output, error) =>
        {
            var mode = parseResult.GetValue(rearm) switch { "once" => RearmMode.Once, "step" => RearmMode.Step, _ => RearmMode.Below };
            var rules = new List<SnapshotRule>();
            if (parseResult.GetValue(every) is { } everyText && TryParseInterval(everyText, out var everyInterval))
            {
                rules.Add(new EveryRule(everyInterval));
            }

            if (parseResult.GetValue(above) is { } aboveText && GrowthThreshold.TryParse(aboveText, out var aboveSize) && aboveSize.Bytes is { } aboveBytes)
            {
                rules.Add(new AboveRule(aboveBytes, mode));
            }

            if (parseResult.GetValue(growth) is { } growthText && GrowthThreshold.TryParse(growthText, out var growthSize))
            {
                rules.Add(new GrowthRule(growthSize.Bytes, growthSize.Percent, mode));
            }

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

            var monitor = services().GetRequiredService<MonitorService>();
            var gate = new object();
            await using var run = monitor.Start(
                parseResult.GetValue(pid),
                new MonitorOptions(TimeSpan.FromSeconds(parseResult.GetValue(interval)), new MonitorRules(rules, parseResult.GetValue(max))),
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

    /// <summary>An interval such as 90s, 5m or 1h.</summary>
    internal static bool TryParseInterval(string text, out TimeSpan interval)
    {
        interval = default;
        var trimmed = text.Trim().ToLowerInvariant();
        if (trimmed.Length < 2 || !double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount <= 0 || !double.IsFinite(amount))
        {
            return false;
        }

        interval = trimmed[^1] switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            _ => default,
        };
        return interval >= TimeSpan.FromSeconds(1);
    }

    private static string TableLine(MonitorSample sample)
    {
        var s = sample.Counters;
        return string.Create(CultureInfo.InvariantCulture,
            $"{s.At.ToLocalTime():HH:mm:ss} {Mb(s.GcHeapBytes),10} {Mb(s.HeapAfterGcBytes),10} {Mb(s.Gen1Bytes),9} {Mb(s.Gen2Bytes),10} {Mb(s.LargeObjectHeapBytes),9} {Mb(s.PinnedObjectHeapBytes),8} {$"{s.Gen0Collections}/{s.Gen1Collections}/{s.Gen2Collections}",10} {Mb(s.AllocatedBytes),10} {s.TimeInGcPercent,6:0.#}%{(sample.HighGcPressure ? "  high GC pressure" : string.Empty)}");
    }

    private static string CsvLine(HeapSample s) => string.Create(CultureInfo.InvariantCulture,
        $"{s.At:O},{s.GcHeapBytes},{s.Gen0Bytes},{s.Gen1Bytes},{s.Gen2Bytes},{s.LargeObjectHeapBytes},{s.PinnedObjectHeapBytes},{s.Gen0Collections},{s.Gen1Collections},{s.Gen2Collections},{s.AllocatedBytes},{s.TimeInGcPercent:0.##},{s.WorkingSetBytes}");
}
