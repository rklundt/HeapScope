using System.Diagnostics;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// Forwards at most one report per <see cref="MinimumInterval"/> to the client (ARCHITECTURE.md 4.5), so progress
/// notifications never flood the pipe. <see cref="Flush"/> always forwards the latest value.
/// </summary>
internal sealed class ThrottledProgress<T>(IProgress<T>? inner, TimeSpan? minimumInterval = null) : IProgress<T>
    where T : class
{
    private readonly object _gate = new();
    private readonly long _intervalTicks = (long)((minimumInterval ?? TimeSpan.FromMilliseconds(100)).TotalSeconds * Stopwatch.Frequency);
    private long _lastSent = long.MinValue;
    private T? _pending;

    public TimeSpan MinimumInterval => TimeSpan.FromSeconds((double)_intervalTicks / Stopwatch.Frequency);

    public void Report(T value)
    {
        if (inner is null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_lastSent != long.MinValue && now - _lastSent < _intervalTicks)
            {
                _pending = value;
                return;
            }

            _lastSent = now;
            _pending = null;
        }

        inner.Report(value);
    }

    /// <summary>Forwards the latest suppressed report, if any.</summary>
    public void Flush()
    {
        T? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }

        if (pending is not null)
        {
            inner?.Report(pending);
        }
    }
}
