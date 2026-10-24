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

    // Top-level operation counts and time per priority (indexed by DispatcherPriority, 0 to 10), written on the UI thread
    // and copied by the probe so a lag can be broken down by what ran during it.
    private readonly long[] _operationCounts = new long[11];
    private readonly long[] _operationTicks = new long[11];

    // The UI thread's CPU time at the previous probe (100 ns units), taken on the UI thread. A lag with little UI thread
    // CPU time means the thread was waiting or not scheduled (CPU contention), not working.
    private long _uiCpuAtLastProbe;

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
        var priority = (int)e.Operation.Priority;
        if (_operationStarts.Count == 0 && priority is >= 0 and < 11)
        {
            Interlocked.Increment(ref _operationCounts[priority]);
            Interlocked.Add(ref _operationTicks[priority], elapsed);
        }

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
        var countsBefore = Snapshot(_operationCounts);
        var ticksBefore = Snapshot(_operationTicks);
        _dispatcher.BeginInvoke(
            () =>
            {
                var lag = Stopwatch.GetTimestamp() - posted;
                var uiCpu = NativeMethods.CurrentThreadCpuTime();
                var uiCpuMs = (uiCpu - _uiCpuAtLastProbe) / 10_000.0;
                _uiCpuAtLastProbe = uiCpu;
                Interlocked.Exchange(ref _probePending, 0);
                if (posted >= Interlocked.Read(ref _resetTimestamp))
                {
                    RecordMax(ref _maxLagTicks, lag);
                }

                if (lag > Budget.TotalSeconds * Stopwatch.Frequency)
                {
                    _logger.LogWarning(
                        "UI dispatcher was blocked for {LagMs:N0} ms (budget {BudgetMs} ms); UI thread CPU {UiCpuMs:N0} ms; UI process GC pauses in that time {GcPauseMs:N0} ms, gen2 collections {Gen2}; ran meanwhile: {Breakdown}",
                        lag * 1000.0 / Stopwatch.Frequency,
                        Budget.TotalMilliseconds,
                        uiCpuMs,
                        (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds,
                        GC.CollectionCount(2) - gen2Before,
                        Breakdown(lag, countsBefore, ticksBefore));
                }
            },
            DispatcherPriority.Input);
    }

    private static long[] Snapshot(long[] values) => [.. values.Select((_, i) => Interlocked.Read(ref values[i]))];

    // "Render 14 ops 420 ms, Normal 3 ops 40 ms, outside operations 120 ms": what kept the input-priority probe waiting.
    private string Breakdown(long lag, long[] countsBefore, long[] ticksBefore)
    {
        var parts = new List<string>();
        long busy = 0;
        for (var p = 10; p >= 0; p--)
        {
            var count = Interlocked.Read(ref _operationCounts[p]) - countsBefore[p];
            var ticks = Interlocked.Read(ref _operationTicks[p]) - ticksBefore[p];
            if (count > 0)
            {
                busy += ticks;
                parts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(DispatcherPriority)p} {count} ops {ticks * 1000.0 / Stopwatch.Frequency:N0} ms"));
            }
        }

        parts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"outside operations {Math.Max(0, lag - busy) * 1000.0 / Stopwatch.Frequency:N0} ms"));
        return string.Join(", ", parts);
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
