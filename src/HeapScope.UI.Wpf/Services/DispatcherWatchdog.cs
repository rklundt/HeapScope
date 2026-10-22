using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.Wpf.Services;

/// <summary>
/// Measures UI responsiveness (ARCHITECTURE.md 8, budget: no dispatcher block over 100 ms). A background timer
/// posts a probe at input priority every <see cref="ProbeInterval"/>; the delay until the probe runs is how long
/// the dispatcher was busy. Lags over the budget are logged; the maximum is kept for smoke tests.
/// </summary>
public sealed class DispatcherWatchdog : IDisposable
{
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(25);

    private readonly Dispatcher _dispatcher;
    private readonly ILogger<DispatcherWatchdog> _logger;
    private readonly Timer _timer;
    private long _maxLagTicks;
    private long _maxOperationTicks;
    private long _resetTimestamp;
    private int _probePending;
    private readonly Stack<long> _operationStarts = new();

    public DispatcherWatchdog(Dispatcher dispatcher, ILogger<DispatcherWatchdog> logger)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timer = new Timer(_ => Probe(), null, ProbeInterval, ProbeInterval);
        _dispatcher.Hooks.OperationStarted += OnOperationStarted;
        _dispatcher.Hooks.OperationCompleted += OnOperationCompleted;
    }

    /// <summary>The longest dispatcher lag observed since start or <see cref="Reset"/>.</summary>
    public TimeSpan MaxLag => TimeSpan.FromSeconds((double)Interlocked.Read(ref _maxLagTicks) / Stopwatch.Frequency);

    /// <summary>The longest single dispatcher operation since start or <see cref="Reset"/> (the budget's actual subject).</summary>
    public TimeSpan MaxOperation => TimeSpan.FromSeconds((double)Interlocked.Read(ref _maxOperationTicks) / Stopwatch.Frequency);

    /// <summary>Clears the maxima; probes posted before the reset do not count toward the new maximum.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _resetTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _maxLagTicks, 0);
        Interlocked.Exchange(ref _maxOperationTicks, 0);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _dispatcher.Hooks.OperationStarted -= OnOperationStarted;
        _dispatcher.Hooks.OperationCompleted -= OnOperationCompleted;
    }

    // Operation timing names the priority of any single dispatcher operation over budget, which tells our own
    // work (Normal, DataBind) apart from WPF layout and rendering (Render, Loaded). Hooks run on the UI thread; a stack handles nested operations.
    private void OnOperationStarted(object? sender, DispatcherHookEventArgs e) => _operationStarts.Push(Stopwatch.GetTimestamp());

    private void OnOperationCompleted(object? sender, DispatcherHookEventArgs e)
    {
        if (!_operationStarts.TryPop(out var started))
        {
            return;
        }

        var elapsed = Stopwatch.GetTimestamp() - started;
        if (started >= Interlocked.Read(ref _resetTimestamp))
        {
            RecordMax(ref _maxOperationTicks, elapsed);
        }

        if (elapsed > Budget.TotalSeconds * Stopwatch.Frequency)
        {
            _logger.LogWarning("A dispatcher operation at {Priority} priority ran for {ElapsedMs:N0} ms", e.Operation.Priority, elapsed * 1000.0 / Stopwatch.Frequency);
        }
    }

    private void Probe()
    {
        // One probe at a time: a blocked dispatcher must not accumulate probes.
        if (Interlocked.Exchange(ref _probePending, 1) == 1)
        {
            return;
        }

        var posted = Stopwatch.GetTimestamp();
        var pauseBefore = GC.GetTotalPauseDuration();
        var gen2Before = GC.CollectionCount(2);
        _dispatcher.BeginInvoke(
            () =>
            {
                var lag = Stopwatch.GetTimestamp() - posted;
                Interlocked.Exchange(ref _probePending, 0);
                if (posted >= Interlocked.Read(ref _resetTimestamp))
                {
                    RecordMax(ref _maxLagTicks, lag);
                }

                if (lag > Budget.TotalSeconds * Stopwatch.Frequency)
                {
                    _logger.LogWarning(
                        "UI dispatcher was blocked for {LagMs:N0} ms (budget {BudgetMs} ms); UI process GC pauses in that time {GcPauseMs:N0} ms, gen2 collections {Gen2}",
                        lag * 1000.0 / Stopwatch.Frequency,
                        Budget.TotalMilliseconds,
                        (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds,
                        GC.CollectionCount(2) - gen2Before);
                }
            },
            DispatcherPriority.Input);
    }

    private static void RecordMax(ref long maximum, long value)
    {
        var current = Interlocked.Read(ref maximum);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref maximum, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
