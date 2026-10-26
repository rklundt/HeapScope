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

using HeapScope.Analysis;
using HeapScope.Analysis.Retention;

namespace HeapScope.Worker.Snapshots;

/// <summary>
/// A caller waiting for a shared derived computation (<see cref="DerivedData"/>). Disposing it leaves; when the last
/// caller leaves a computation that is still running, the computation is canceled after a short grace period (the 2.3
/// review M13), unless another caller joins meanwhile.
/// </summary>
internal sealed class DerivedWaiter<T>(Task<T> task, Action leave) : IDisposable
{
    private int _left;

    /// <summary>The shared computation.</summary>
    public Task<T> Task { get; } = task;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _left, 1) == 0)
        {
            leave();
        }
    }
}

/// <summary>
/// Data computed from a loaded snapshot on first use and dropped with it in one step when the snapshot closes
/// (sprint 1.3: the type aggregation; sprint 2.1: the reverse references index; sprint 2.2: the root path tree; sprint
/// 2.3: the dominators and the retained sizes per grouping; later detector results get their slots here). Each slot holds
/// one shared computation for the callers waiting for it: it keeps running while any caller waits (a caller that cancels
/// only stops waiting), and is canceled when the last one leaves before it finishes and nobody joins within the grace
/// period (2.3 review M13), or when the snapshot closes. The next caller then starts it again. Indices that own memory or
/// a mapping are attached to the graph, which releases them after the last lease, so dropping a slot here never frees
/// data a query still reads.
/// </summary>
/// <param name="grace">
/// How long a computation nobody waits for keeps running before it is canceled: long enough for a grid's re-query (a
/// filter keystroke cancels the previous query, then asks again) to join it instead of starting over.
/// </param>
internal sealed class DerivedData(TimeSpan? grace = null)
{
    /// <summary>The default grace period of a computation nobody waits for.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(2);

    private readonly Lock _lock = new();
    private readonly TimeSpan _grace = grace ?? DefaultGrace;

    // Canceled on close, never disposed: a computation still running may register on its token.
    private readonly CancellationTokenSource _lifetime = new();
    private Slot<TypeAggregation> _aggregation = new();
    private Slot<ReverseIndex> _reverse = new();
    private Slot<RootPathTree> _rootPaths = new();
    private Slot<DominatorIndex> _dominators = new();
    private readonly Dictionary<GroupKey, Slot<ulong[]>> _groupRetention = [];

    /// <summary>Joins the aggregation, starting it when there is none (or it failed or was canceled).</summary>
    public DerivedWaiter<TypeAggregation> JoinAggregation(Func<CancellationToken, Task<TypeAggregation>> compute) => Join(_aggregation, compute);

    /// <summary>Joins the reverse references index, starting it when there is none (or it failed or was canceled).</summary>
    public DerivedWaiter<ReverseIndex> JoinReverseReferences(Func<CancellationToken, Task<ReverseIndex>> compute) => Join(_reverse, compute);

    /// <summary>Progress of the reverse references computation, sent to every caller waiting for it (review M7).</summary>
    public SharedProgress<Contracts.JobProgress> ReverseProgress { get; } = new();

    /// <summary>Joins the root path tree (sprint 2.2), starting it when there is none (or it failed or was canceled).</summary>
    public DerivedWaiter<RootPathTree> JoinRootPaths(Func<CancellationToken, Task<RootPathTree>> compute) => Join(_rootPaths, compute);

    /// <summary>Progress of the root path tree computation, sent to every caller waiting for it.</summary>
    public SharedProgress<Contracts.JobProgress> RootPathProgress { get; } = new();

    /// <summary>Joins the dominators (sprint 2.3), starting them when there are none (or they failed or were canceled).</summary>
    public DerivedWaiter<DominatorIndex> JoinDominators(Func<CancellationToken, Task<DominatorIndex>> compute) => Join(_dominators, compute);

    /// <summary>Progress of the dominators computation, sent to every caller waiting for it.</summary>
    public SharedProgress<Contracts.JobProgress> DominatorProgress { get; } = new();

    /// <summary>Joins the retained sizes per group of one grouping (sprint 2.3), each computed once.</summary>
    public DerivedWaiter<ulong[]> JoinGroupRetention(GroupKey grouping, Func<CancellationToken, Task<ulong[]>> compute)
    {
        Slot<ulong[]> slot;
        lock (_lock)
        {
            if (!_groupRetention.TryGetValue(grouping, out slot!))
            {
                _groupRetention[grouping] = slot = new Slot<ulong[]>();
            }
        }

        return Join(slot, compute);
    }

    /// <summary>Stops running computations and drops the results.</summary>
    public void Close()
    {
        lock (_lock)
        {
            _lifetime.Cancel();
            _aggregation = new();
            _reverse = new();
            _rootPaths = new();
            _dominators = new();
            _groupRetention.Clear();
        }
    }

    private DerivedWaiter<T> Join<T>(Slot<T> slot, Func<CancellationToken, Task<T>> compute)
    {
        lock (_lock)
        {
            // A computation that failed, was canceled, or is being canceled because nobody waited is started again; after
            // close, the canceled one is returned, so callers see the snapshot is gone.
            var stale = slot.Task is null || slot.Task.IsFaulted
                || ((slot.Task.IsCanceled || slot.Abandoned) && !_lifetime.IsCancellationRequested);
            if (stale)
            {
                slot.Source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                slot.Task = compute(slot.Source.Token);
                slot.Waiters = 0;
                slot.Abandoned = false;
            }

            slot.Waiters++;
            var source = slot.Source!;
            return new DerivedWaiter<T>(slot.Task!, () => Leave(slot, source));
        }
    }

    private void Leave<T>(Slot<T> slot, CancellationTokenSource source)
    {
        lock (_lock)
        {
            // A newer computation replaced this one: its waiters are counted there.
            if (slot.Source != source || --slot.Waiters > 0 || slot.Task!.IsCompleted)
            {
                return;
            }
        }

        if (_grace <= TimeSpan.Zero)
        {
            CancelIfAbandoned(slot, source);
            return;
        }

        _ = System.Threading.Tasks.Task.Delay(_grace).ContinueWith(
            _ => CancelIfAbandoned(slot, source),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Cancels the computation when still nobody waits for it and it has not finished. It is marked abandoned under the
    /// lock, so a caller joining before the cancellation lands starts a new computation instead of joining a canceled
    /// one; the cancellation itself runs outside the lock, since its callbacks may leave other slots.
    /// </summary>
    private void CancelIfAbandoned<T>(Slot<T> slot, CancellationTokenSource source)
    {
        lock (_lock)
        {
            if (slot.Source != source || slot.Waiters > 0 || slot.Task!.IsCompleted)
            {
                return;
            }

            slot.Abandoned = true;
        }

        source.Cancel();
    }

    /// <summary>One shared computation, its own cancellation (linked to the snapshot's lifetime) and its waiters.</summary>
    private sealed class Slot<T>
    {
        public Task<T>? Task { get; set; }

        public CancellationTokenSource? Source { get; set; }

        public int Waiters { get; set; }

        /// <summary>Canceled, or about to be, because nobody waited for it: the next caller starts it again.</summary>
        public bool Abandoned { get; set; }
    }
}
