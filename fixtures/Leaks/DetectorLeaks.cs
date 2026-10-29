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

namespace Fixtures.Leaks;

// The leaks sprint 2.6's detectors name besides the classic five (fixtures/KnownAnswers): kept out of LeakCatalog.All,
// whose exact census and paths the earlier sprints check, and enabled with LeakyApi's Leaks:Detected and LeakyConsole's
// --detected (LeakCatalog.Detected).

/// <summary>
/// Leak 6: started <see cref="System.Timers.Timer"/>s never stopped or disposed. The runtime's timer queue holds every
/// started timer, so each one keeps its <c>Elapsed</c> handlers' targets alive with no reference from the application
/// (a <see cref="System.Threading.Timer"/> nothing references is finalized and stops, so it does not leak this way).
/// </summary>
public sealed class TimerLeak : ILeak
{
    private static readonly object s_gate = new();
    private static int s_count;

    public string Name => "Timer";

    public int ItemCount => Volatile.Read(ref s_count);

    public void Grow(int items, int payloadBytes)
    {
        lock (s_gate)
        {
            for (var i = 0; i < items; i++)
            {
                var target = new TimerTarget(new byte[payloadBytes]);

                // An hour's interval: started, so queued, and it never fires during a test.
                var timer = new System.Timers.Timer(TimeSpan.FromHours(1).TotalMilliseconds) { AutoReset = true };
                timer.Elapsed += target.OnElapsed;
                timer.Start();
                s_count++;
            }
        }
    }
}

/// <summary>The object a leaked timer's handler keeps alive.</summary>
public sealed class TimerTarget(byte[] state)
{
    public byte[] State { get; } = state;

    public int Ticks { get; private set; }

    public void OnElapsed(object? sender, System.Timers.ElapsedEventArgs e) => Ticks++;
}

/// <summary>Leak 7: objects disposed and then kept in a static list (disposed but referenced).</summary>
public sealed class DisposedReferencedLeak : ILeak
{
    private static readonly List<DisposedResource> s_disposed = [];
    private static readonly object s_gate = new();

    public string Name => "DisposedReferenced";

    public int ItemCount
    {
        get
        {
            lock (s_gate)
            {
                return s_disposed.Count;
            }
        }
    }

    public void Grow(int items, int payloadBytes)
    {
        lock (s_gate)
        {
            for (var i = 0; i < items; i++)
            {
                var resource = new DisposedResource(new byte[payloadBytes]);
                resource.Dispose();
                s_disposed.Add(resource);
            }
        }
    }
}

/// <summary>A disposable with the disposed flag the .NET dispose pattern template uses.</summary>
public sealed class DisposedResource(byte[] buffer) : IDisposable
{
    private bool _disposedValue;

    public byte[] Buffer { get; } = buffer;

    public bool IsDisposed => _disposedValue;

    public void Dispose() => _disposedValue = true;
}

/// <summary>
/// The clean workload (sprint 2.6): the same kinds of work done right, for the run where no detector may fire. A cache
/// bounded at <see cref="CacheLimit"/> entries that evicts the oldest, subscribers that unsubscribe, timers disposed,
/// disposables disposed and released. The first run also sets up, once and above each detector's threshold, what looks
/// like a leak and is not (review M14): objects keeping their own handler, unevaluated lazies, pending one-shot timers
/// (<c>Task.Delay</c>, <c>CancelAfter</c>) and HttpClients sharing one handler.
/// </summary>
public static class CleanWorkload
{
    /// <summary>Below the growth detector's 1,000 (review L24), so the bounded cache never looks like growth.</summary>
    public const int CacheLimit = 500;

    /// <summary>How many of each steady pattern the first run sets up: above every detector's threshold.</summary>
    public const int SteadyCount = 1_500;

    private static object[]? s_steady;

    private static readonly object s_gate = new();
    private static readonly Dictionary<int, byte[]> s_cache = [];
    private static readonly Queue<int> s_order = new();
    private static readonly LongLivedPublisher s_publisher = new();
    private static int s_next;

    /// <summary>Requests served so far (tests).</summary>
    public static int Runs { get; private set; }

    public static void Run(int items, int payloadBytes)
    {
        lock (s_gate)
        {
            s_steady ??= Steady(payloadBytes);
            for (var i = 0; i < items; i++)
            {
                var key = s_next++;
                s_cache[key] = new byte[payloadBytes];
                s_order.Enqueue(key);
                if (s_order.Count > CacheLimit)
                {
                    s_cache.Remove(s_order.Dequeue());
                }

                var subscriber = new LeakySubscriber(new byte[payloadBytes]);
                s_publisher.Changed += subscriber.OnChanged;
                s_publisher.Changed -= subscriber.OnChanged;

                var target = new TimerTarget(new byte[payloadBytes]);
                using (var timer = new System.Timers.Timer(TimeSpan.FromHours(1).TotalMilliseconds))
                {
                    timer.Elapsed += target.OnElapsed;
                    timer.Start();
                    timer.Stop();
                }

                using (new DisposedResource(new byte[payloadBytes]))
                {
                }
            }

            Runs++;
        }
    }

    private static async Task<int> FinishedAsync(int value)
    {
        await Task.Yield();
        return value;
    }

    private static object[] Steady(int payloadBytes)
    {
        var steady = new List<object>();
        var publisher = new LongLivedPublisher();
        var handler = new System.Net.Http.HttpClientHandler();
        for (var i = 0; i < SteadyCount; i++)
        {
            // Keeps its own handler to unsubscribe later: a delegate field, not an event holding it.
            var owner = new HandlerOwner(new byte[payloadBytes]);
            publisher.Changed += owner.Handler;
            publisher.Changed -= owner.Handler;
            steady.Add(owner);
            steady.Add(new Lazy<byte[]>(() => new byte[payloadBytes]));
            if (i < 600)
            {
                // One-shot timers pending for an hour: they release themselves when they fire.
                steady.Add(Task.Delay(TimeSpan.FromHours(1)));
                var source = new CancellationTokenSource();
                source.CancelAfter(TimeSpan.FromHours(1));
                steady.Add(source);
            }

            if (i < 25)
            {
                steady.Add(new System.Net.Http.HttpClient(handler, disposeHandler: false));
            }

            // Async calls that finished, their tasks kept (sprint 3.5): state machines that are not waiting.
            steady.Add(FinishedAsync(i));
        }

        steady.Add(handler);
        return [.. steady];
    }
}

/// <summary>An object that keeps its handler in a field so it can unsubscribe the same delegate later.</summary>
public sealed class HandlerOwner
{
    public readonly EventHandler Handler;

    public HandlerOwner(byte[] state)
    {
        State = state;
        Handler = OnChanged;
    }

    public byte[] State { get; }

    private void OnChanged(object? sender, EventArgs e)
    {
    }
}
