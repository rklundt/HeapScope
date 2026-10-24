using System.Diagnostics.Tracing;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace HeapScope.Capture;

/// <summary>
/// One interval of a process's runtime counters (sprint 1.7). <see cref="GcHeapBytes"/> is the heap now, including
/// everything allocated since the last GC; the generation sizes are as of the last GC (they change only when one runs).
/// Collections and allocations are counted during the interval; <see cref="TimeInGcPercent"/> is the share the last GC
/// took of the time since the one before (it does not change while no GC runs).
/// </summary>
public sealed record CounterSample(
    DateTimeOffset At,
    long GcHeapBytes,
    long Gen0Bytes,
    long Gen1Bytes,
    long Gen2Bytes,
    long LargeObjectHeapBytes,
    long PinnedObjectHeapBytes,
    long Gen0Collections,
    long Gen1Collections,
    long Gen2Collections,
    long AllocatedBytes,
    double TimeInGcPercent,
    long WorkingSetBytes);

/// <summary>
/// Turns the runtime's counter values, which arrive one event per counter per interval, into samples: a counter seen
/// again starts the next interval, so each sample is complete when the next one begins (testable without a process).
/// </summary>
public sealed class CounterSampleBuilder
{
    /// <summary>The runtime publishes gc-heap-size and working-set in decimal megabytes (bytes / 1,000,000).</summary>
    private const double Megabyte = 1_000_000;

    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);
    private DateTimeOffset _at;

    /// <summary>Adds one counter value; returns the previous interval's sample when this value starts a new one.</summary>
    public CounterSample? Add(string name, double value, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(name);
        CounterSample? finished = null;
        if (_values.ContainsKey(name))
        {
            finished = Flush();
        }

        if (_values.Count == 0)
        {
            _at = at;
        }

        _values[name] = value;
        return finished;
    }

    /// <summary>
    /// The sample being collected, if any. One without the GC heap size (the last, cut-short interval when the session
    /// ends) is dropped: its missing counters would read as zeros (review of 1.7).
    /// </summary>
    public CounterSample? Flush()
    {
        if (_values.Count == 0)
        {
            return null;
        }

        if (!_values.ContainsKey("gc-heap-size"))
        {
            _values.Clear();
            return null;
        }

        long Bytes(string name) => (long)_values.GetValueOrDefault(name);
        long Megabytes(string name) => (long)(_values.GetValueOrDefault(name) * Megabyte);
        var sample = new CounterSample(
            _at,
            Megabytes("gc-heap-size"),
            Bytes("gen-0-size"),
            Bytes("gen-1-size"),
            Bytes("gen-2-size"),
            Bytes("loh-size"),
            Bytes("poh-size"),
            Bytes("gen-0-gc-count"),
            Bytes("gen-1-gc-count"),
            Bytes("gen-2-gc-count"),
            Bytes("alloc-rate"),
            _values.GetValueOrDefault("time-in-gc"),
            Megabytes("working-set"));
        _values.Clear();
        return sample;
    }
}

/// <summary>
/// Reads a process's runtime counters (<c>System.Runtime</c> EventCounters) over an EventPipe session (sprint 1.7,
/// ADR-021): the diagnostics client HeapScope already uses for capture and traces, so nothing is installed and the
/// target needs no changes.
/// </summary>
public static class CounterMonitor
{
    private const string RuntimeProvider = "System.Runtime";

    /// <summary>
    /// Reads samples every <paramref name="interval"/> until canceled or the process exits, handing each to
    /// <paramref name="onSample"/> (on a background thread).
    /// </summary>
    /// <exception cref="TargetUnavailableException">The process is gone or does not answer on its diagnostics port.</exception>
    /// <exception cref="ElevationRequiredException">The process belongs to another user or is elevated.</exception>
    public static async Task RunAsync(int processId, TimeSpan interval, Action<CounterSample> onSample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onSample);
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.FromSeconds(1));
        var providers = new[]
        {
            new EventPipeProvider(
                RuntimeProvider,
                EventLevel.Informational,
                -1,
                new Dictionary<string, string> { ["EventCounterIntervalSec"] = ((int)interval.TotalSeconds).ToString(CultureInfo.InvariantCulture) }),
        };

        EventPipeSession session;
        try
        {
            session = await new DiagnosticsClient(processId).StartEventPipeSessionAsync(providers, requestRundown: false, circularBufferMB: 16, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ServerNotAvailableException or ServerErrorException or UnauthorizedAccessException)
        {
            throw Translate(processId, ex);
        }

        using (session)
        {
            var builder = new CounterSampleBuilder();
            var reading = Task.Run(
                () =>
                {
                    using var source = new EventPipeEventSource(session.EventStream);
                    source.Dynamic.All += data =>
                    {
                        if (TryRead(data, out var name, out var value) && builder.Add(name, value, DateTimeOffset.UtcNow) is { } sample)
                        {
                            onSample(sample);
                        }
                    };
                    source.Process();
                    if (builder.Flush() is { } last)
                    {
                        onSample(last);
                    }
                },
                CancellationToken.None);

            using (cancellationToken.Register(() => _ = StopQuietlyAsync(session)))
            {
                await reading.ConfigureAwait(false);
            }
        }
    }

    /// <summary>The counter name and value of an <c>EventCounters</c> event (the mean of a gauge, the increment of a rate).</summary>
    public static bool TryRead(TraceEvent data, out string name, out double value)
    {
        ArgumentNullException.ThrowIfNull(data);
        name = string.Empty;
        value = 0;
        if (data.EventName != "EventCounters" || data.PayloadValue(0) is not IDictionary<string, object> outer
            || !outer.TryGetValue("Payload", out var inner) || inner is not IDictionary<string, object> fields)
        {
            return false;
        }

        return TryRead(fields, out name, out value);
    }

    /// <summary>The counter name and value of an EventCounters payload.</summary>
    public static bool TryRead(IDictionary<string, object> fields, out string name, out double value)
    {
        ArgumentNullException.ThrowIfNull(fields);
        name = fields.TryGetValue("Name", out var n) && n is string text ? text : string.Empty;
        value = 0;
        if (name.Length == 0)
        {
            return false;
        }

        var raw = fields.TryGetValue("Mean", out var mean) ? mean : fields.TryGetValue("Increment", out var increment) ? increment : null;
        if (raw is null)
        {
            return false;
        }

        value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// Stops the session; a target that does not answer within 5 seconds (frozen in a debugger) has its session disposed,
    /// which ends the stream, so stopping never hangs (review of 1.7).
    /// </summary>
    private static async Task StopQuietlyAsync(EventPipeSession session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await session.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            session.Dispose();
        }
        catch (Exception ex) when (ex is ServerNotAvailableException or ServerErrorException or EndOfStreamException or IOException or ObjectDisposedException)
        {
            // The process exited, or the session already ended: the stream is over either way.
        }
    }

    private static Exception Translate(int processId, Exception ex)
    {
        string processName;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            processName = process.ProcessName;
        }
        catch (Exception lookup) when (lookup is ArgumentException or InvalidOperationException)
        {
            return new TargetUnavailableException($"No running process has id {processId}.", ex);
        }

        return ex is UnauthorizedAccessException
            ? ElevationRequiredException.For(processId, processName, ex)
            : new TargetUnavailableException($"{processName} ({processId}) did not answer on its diagnostics port: {ex.Message} Is it a .NET 6 or later process?", ex);
    }
}
