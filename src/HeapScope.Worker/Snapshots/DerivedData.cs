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
/// Data computed from a loaded snapshot on first use and dropped with it in one step when the snapshot closes
/// (sprint 1.3: the type aggregation; sprint 2.1: the reverse references index; sprint 2.2: the root path tree; later
/// dominators and detector results get their slots here). Each slot holds one shared computation that runs for the
/// snapshot's lifetime, not for the caller that started it. Indices that own memory or a mapping are attached to the graph, which releases them after the last
/// lease, so dropping a slot here never frees data a query still reads.
/// </summary>
internal sealed class DerivedData
{
    private readonly Lock _lock = new();

    // Canceled on close, never disposed: a computation still running may register on its token.
    private readonly CancellationTokenSource _lifetime = new();
    private Task<TypeAggregation>? _aggregation;
    private Task<ReverseIndex>? _reverse;
    private Task<RootPathTree>? _rootPaths;

    /// <summary>The running or finished aggregation; a failed or canceled one is started again.</summary>
    public Task<TypeAggregation> GetOrStartAggregation(Func<CancellationToken, Task<TypeAggregation>> compute) =>
        GetOrStart(ref _aggregation, compute);

    /// <summary>The running or finished reverse references index; a failed or canceled one is started again.</summary>
    public Task<ReverseIndex> GetOrStartReverseReferences(Func<CancellationToken, Task<ReverseIndex>> compute) =>
        GetOrStart(ref _reverse, compute);

    /// <summary>Progress of the reverse references computation, sent to every caller waiting for it (review M7).</summary>
    public SharedProgress<Contracts.JobProgress> ReverseProgress { get; } = new();

    /// <summary>The running or finished root path tree (sprint 2.2); a failed or canceled one is started again.</summary>
    public Task<RootPathTree> GetOrStartRootPaths(Func<CancellationToken, Task<RootPathTree>> compute) =>
        GetOrStart(ref _rootPaths, compute);

    /// <summary>Progress of the root path tree computation, sent to every caller waiting for it.</summary>
    public SharedProgress<Contracts.JobProgress> RootPathProgress { get; } = new();

    /// <summary>Stops running computations and drops the results.</summary>
    public void Close()
    {
        lock (_lock)
        {
            _lifetime.Cancel();
            _aggregation = null;
            _reverse = null;
            _rootPaths = null;
        }
    }

    private Task<T> GetOrStart<T>(ref Task<T>? slot, Func<CancellationToken, Task<T>> compute)
    {
        lock (_lock)
        {
            if (slot is null || slot.IsFaulted || (slot.IsCanceled && !_lifetime.IsCancellationRequested))
            {
                slot = compute(_lifetime.Token);
            }

            return slot;
        }
    }
}
