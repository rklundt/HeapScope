using System.Globalization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace HeapScope.Benchmarks;

/// <summary>
/// Developer tool (sprint 1.5): what the UI thread was busy with in a <c>.nettrace</c> from <c>--smoke-trace</c> or the
/// CLI's <c>trace</c>. Prints the busiest stretches of the UI thread with their heaviest methods (inclusive samples,
/// leaving out frames every busy sample shares), and the garbage collection pauses. Reading traces is not shipped in the
/// product; allocation tracking (sprint 2.5) brings it in.
/// Usage: trace-report --trace &lt;file.nettrace&gt; [--top 25] [--windows 6] [--window-ms 250] [--thread &lt;id&gt;]
/// [--focus &lt;frame text&gt;] (only busy samples with that frame, and their callers right above it).
/// </summary>
internal static class TraceReport
{
    private const string SampleProvider = "Microsoft-DotNETCore-SampleProfiler";
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";

    public static int Run(BenchmarkArgs args)
    {
        var path = args.Get("trace") ?? throw new ArgumentException("--trace <file.nettrace> is required.");
        var top = int.Parse(args.Get("top") ?? "25", CultureInfo.InvariantCulture);
        var windows = int.Parse(args.Get("windows") ?? "6", CultureInfo.InvariantCulture);
        var windowMs = double.Parse(args.Get("window-ms") ?? "250", CultureInfo.InvariantCulture);
        var etlx = TraceLog.CreateFromEventPipeDataFile(path);
        using var log = new TraceLog(etlx);

        var samples = new List<Sample>();
        var gcSuspend = new Dictionary<int, double>();
        var pauses = new List<(double At, double Ms, string Kind)>();
        foreach (var ev in log.Events)
        {
            if (ev.ProviderName == SampleProvider)
            {
                var frames = new List<string>();
                for (var frame = ev.CallStack(); frame is not null; frame = frame.Caller)
                {
                    var name = frame.CodeAddress.FullMethodName;
                    frames.Add(string.IsNullOrEmpty(name) ? "?!" + frame.CodeAddress.ModuleName : name);
                }

                samples.Add(new Sample(ev.ThreadID, ev.TimeStampRelativeMSec, frames));
            }
            else if (ev.ProviderName == RuntimeProvider)
            {
                switch (ev.EventName)
                {
                    // Reasons 1 (SuspendForGC) and 6 (SuspendForGCPrep); the sample profiler suspends the runtime too.
                    case "GC/SuspendEEStart" when Convert.ToInt32(ev.PayloadByName("Reason"), CultureInfo.InvariantCulture) is 1 or 6:
                        gcSuspend[ev.ProcessID] = ev.TimeStampRelativeMSec;
                        break;
                    case "GC/RestartEEStop" when gcSuspend.Remove(ev.ProcessID, out var start):
                        pauses.Add((start, ev.TimeStampRelativeMSec - start, "pause"));
                        break;
                }
            }
        }

        // The UI thread is the one whose stacks run the WPF dispatcher most.
        var uiThread = args.Get("thread") is { } chosen
            ? int.Parse(chosen, CultureInfo.InvariantCulture)
            : samples.Where(s => s.Frames.Exists(f => f.Contains("System.Windows.Threading.Dispatcher", StringComparison.Ordinal)))
                .GroupBy(s => s.Thread).MaxBy(g => g.Count())?.Key ?? -1;
        var ui = samples.Where(s => s.Thread == uiThread).ToList();
        var busy = ui.Where(s => !IsIdle(s)).ToList();
        if (args.Get("focus") is { } focus)
        {
            var focused = busy.Where(s => s.Frames.Exists(f => f.Contains(focus, StringComparison.Ordinal))).ToList();
            Console.WriteLine($"{focused.Count:N0} of {busy.Count:N0} busy UI samples contain {focus}; their nearest callers:");
            foreach (var chain in focused
                .Select(s => string.Join(" <- ", s.Frames.SkipWhile(f => !f.Contains(focus, StringComparison.Ordinal)).Take(8).Select(Shorten)))
                .GroupBy(c => c, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Take(top))
            {
                Console.WriteLine($"  {chain.Count(),5}  {chain.Key}");
            }

            return 0;
        }

        Console.WriteLine($"{path}: {samples.Count:N0} samples; UI thread {uiThread}: {ui.Count:N0} samples, {busy.Count:N0} busy (about 1 ms each)");

        var origin = log.SessionStartTime;
        if (args.Get("from") is { } from && args.Get("to") is { } to)
        {
            // Only the busy samples between two wall-clock times (HH:mm:ss.fff), as the log prints them.
            double At(string text) => (origin.Date + TimeSpan.Parse(text, CultureInfo.InvariantCulture) - origin).TotalMilliseconds;
            var (start, end) = (At(from), At(to));
            busy = [.. busy.Where(s => s.Ms >= start && s.Ms < end)];
            Console.WriteLine($"{busy.Count:N0} busy UI samples from {from} to {to}");
            PrintHeaviest(busy, top);
            return 0;
        }

        if (args.Get("timeline") is not null)
        {
            // Busy UI samples per window, in time order: where the stretches are.
            foreach (var window in busy.GroupBy(s => Math.Floor(s.Ms / windowMs)).OrderBy(g => g.Key))
            {
                Console.WriteLine($"  {origin.AddMilliseconds(window.Key * windowMs):HH:mm:ss.fff}  {window.Count(),5}  {new string('#', Math.Min(80, window.Count() / 2))}");
            }

            return 0;
        }

        var stretches = busy
            .GroupBy(s => Math.Floor(s.Ms / windowMs))
            .OrderByDescending(g => g.Count())
            .Take(windows)
            .OrderBy(g => g.Key)
            .ToList();
        foreach (var stretch in stretches)
        {
            var at = origin.AddMilliseconds(stretch.Key * windowMs);
            Console.WriteLine();
            Console.WriteLine($"== {at:HH:mm:ss.fff} +{windowMs:N0} ms: {stretch.Count()} busy samples");
            PrintHeaviest([.. stretch], top);
        }

        Console.WriteLine();
        Console.WriteLine("== All busy UI samples");
        PrintHeaviest(busy, top);

        Console.WriteLine();
        Console.WriteLine($"== Garbage collection pauses over 10 ms ({pauses.Count} pauses, {pauses.Sum(p => p.Ms):N0} ms in all)");
        foreach (var (at, ms, _) in pauses.Where(p => p.Ms > 10).OrderBy(p => p.At))
        {
            Console.WriteLine($"  {origin.AddMilliseconds(at):HH:mm:ss.fff}  {ms,7:N1} ms");
        }

        return 0;
    }

    // An idle WPF UI thread waits for messages (in managed frames, Dispatcher.GetMessage on top).
    private static bool IsIdle(Sample sample) => sample.Frames.Take(4).Any(f =>
        f.Contains("Dispatcher.GetMessage", StringComparison.Ordinal)
        || f.Contains("GetMessageW", StringComparison.Ordinal)
        || f.Contains("WaitMessage", StringComparison.Ordinal)
        || f.Contains("MsgWaitForMultipleObjects", StringComparison.Ordinal)
        || f.Contains("WaitHandle.WaitOne", StringComparison.Ordinal));

    private static void PrintHeaviest(List<Sample> samples, int top)
    {
        if (samples.Count == 0)
        {
            return;
        }

        var inclusive = new Dictionary<string, int>(StringComparer.Ordinal);
        var exclusive = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            foreach (var frame in sample.Frames.Distinct(StringComparer.Ordinal))
            {
                inclusive[frame] = inclusive.GetValueOrDefault(frame) + 1;
            }

            if (sample.Frames.Count > 0)
            {
                exclusive[sample.Frames[0]] = exclusive.GetValueOrDefault(sample.Frames[0]) + 1;
            }
        }

        // Frames nearly every sample shares (the dispatcher loop, Main) say nothing about this stretch.
        foreach (var (frame, count) in inclusive.Where(f => f.Value < samples.Count * 0.95).OrderByDescending(f => f.Value).Take(top))
        {
            Console.WriteLine($"  {count,6} incl {exclusive.GetValueOrDefault(frame),6} self  {Shorten(frame)}");
        }
    }

    private static string Shorten(string frame) => frame.Length <= 160 ? frame : frame[..157] + "...";

    private sealed record Sample(int Thread, double Ms, List<string> Frames);
}
