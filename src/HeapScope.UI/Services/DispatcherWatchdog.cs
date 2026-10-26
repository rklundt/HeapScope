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
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.Services;

/// <summary>
/// Measures UI responsiveness (ARCHITECTURE.md 8, budget: no UI thread block over 100 ms). A background timer posts two
/// probes every <see cref="ProbeInterval"/>: one at input priority, whose delay is how long input would have waited
/// (<see cref="MaxLag"/>), and one at the highest priority, which waits only for the operation already running, so its
/// delay bounds the longest single operation (<see cref="MaxOperation"/>; Avalonia's dispatcher has no operation hooks,
/// sprint 2.3.6). Lags over the budget are logged; the maxima are kept for the smoke runs.
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
    private int _lagProbePending;
    private int _operationProbePending;

    // The UI thread's CPU time at the previous lag probe (100 ns units, Windows only). A lag with little UI thread CPU time
    // means the thread was waiting or not scheduled (CPU contention), not working.
    private long _uiCpuAtLastProbe;

    public DispatcherWatchdog(Dispatcher dispatcher, ILogger<DispatcherWatchdog> logger)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timer = new Timer(_ => Probe(), null, ProbeInterval, ProbeInterval);
    }

    /// <summary>The longest input-priority wait observed since start or <see cref="Reset"/>.</summary>
    public TimeSpan MaxLag => TimeSpan.FromSeconds((double)Interlocked.Read(ref _maxLagTicks) / Stopwatch.Frequency);

    /// <summary>The longest highest-priority wait, an upper bound of the longest single UI operation (the budget's subject).</summary>
    public TimeSpan MaxOperation => TimeSpan.FromSeconds((double)Interlocked.Read(ref _maxOperationTicks) / Stopwatch.Frequency);

    /// <summary>Clears the maxima; probes posted before the reset do not count toward the new maximum.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _resetTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _maxLagTicks, 0);
        Interlocked.Exchange(ref _maxOperationTicks, 0);
    }

    public void Dispose() => _timer.Dispose();

    private void Probe()
    {
        // One probe of each kind at a time: a blocked dispatcher must not accumulate probes.
        if (Interlocked.Exchange(ref _operationProbePending, 1) == 0)
        {
            var posted = Stopwatch.GetTimestamp();
            _dispatcher.Post(
                () =>
                {
                    Interlocked.Exchange(ref _operationProbePending, 0);
                    var wait = Stopwatch.GetTimestamp() - posted;
                    if (posted >= Interlocked.Read(ref _resetTimestamp))
                    {
                        RecordMax(ref _maxOperationTicks, wait);
                    }

                    if (wait > Budget.TotalSeconds * Stopwatch.Frequency)
                    {
                        _logger.LogWarning("A UI operation ran for at least {ElapsedMs:N0} ms", wait * 1000.0 / Stopwatch.Frequency);
                    }
                },
                DispatcherPriority.Send);
        }

        if (Interlocked.Exchange(ref _lagProbePending, 1) == 1)
        {
            return;
        }

        var lagPosted = Stopwatch.GetTimestamp();
        var pauseBefore = GC.GetTotalPauseDuration();
        var gen2Before = GC.CollectionCount(2);
        _dispatcher.Post(
            () =>
            {
                var lag = Stopwatch.GetTimestamp() - lagPosted;
                var uiCpu = OperatingSystem.IsWindows() ? NativeMethods.CurrentThreadCpuTime() : 0;
                var uiCpuMs = (uiCpu - _uiCpuAtLastProbe) / 10_000.0;
                _uiCpuAtLastProbe = uiCpu;
                Interlocked.Exchange(ref _lagProbePending, 0);
                if (lagPosted >= Interlocked.Read(ref _resetTimestamp))
                {
                    RecordMax(ref _maxLagTicks, lag);
                }

                if (lag > Budget.TotalSeconds * Stopwatch.Frequency)
                {
                    _logger.LogWarning(
                        "UI thread was blocked for {LagMs:N0} ms (budget {BudgetMs} ms); UI thread CPU {UiCpuMs:N0} ms (Windows only); UI process GC pauses in that time {GcPauseMs:N0} ms, gen2 collections {Gen2}",
                        lag * 1000.0 / Stopwatch.Frequency,
                        Budget.TotalMilliseconds,
                        uiCpuMs,
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
