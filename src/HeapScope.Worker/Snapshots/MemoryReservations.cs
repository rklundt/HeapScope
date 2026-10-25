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
using System.Globalization;
using HeapScope.Storage;

namespace HeapScope.Worker.Snapshots;

/// <summary>
/// The worker's memory check for computations with large working arrays (the root path tree, the dominators, the
/// retained sizes per group; sprint 2.3 decision 4, review M2). A computation reserves what it will allocate before it
/// starts; the check counts what the worker commits plus what running computations have reserved, so two first uses at
/// once (the types grid's retained column while an object's paths load) cannot both pass against the same reading. The
/// count is conservative: a running computation's own allocations are in both. One instance per worker.
/// </summary>
internal sealed class MemoryReservations(Func<MemoryBudget>? budget = null, Func<long>? committedBytes = null)
{
    private readonly Lock _gate = new();
    private readonly Func<MemoryBudget> _budget = budget ?? (() => MemoryBudget.Current());
    private readonly Func<long> _committed = committedBytes ?? (() => CommittedBytes());
    private long _reserved;

    /// <summary>Bytes reserved by running computations.</summary>
    public long Reserved
    {
        get
        {
            lock (_gate)
            {
                return _reserved;
            }
        }
    }

    /// <summary>
    /// Reserves <paramref name="bytes"/> for <paramref name="what"/> until the result is disposed, or throws
    /// <see cref="InsufficientMemoryException"/> with a next step that fits the snapshot (<paramref name="graphMode"/>,
    /// <paramref name="hasCache"/>: a mapped open needs an index cache).
    /// </summary>
    public IDisposable Reserve(long bytes, string what, MemoryMode graphMode, bool hasCache)
    {
        lock (_gate)
        {
            if (Refusal(bytes, what, _budget(), _committed() + _reserved, graphMode, hasCache) is { } refusal)
            {
                throw new InsufficientMemoryException(refusal);
            }

            _reserved += bytes;
        }

        return new Reservation(this, bytes);
    }

    /// <summary>Throws like <see cref="Reserve"/> when <paramref name="bytes"/> would not fit now, reserving nothing.</summary>
    public void Check(long bytes, string what, MemoryMode graphMode, bool hasCache) =>
        Reserve(bytes, what, graphMode, hasCache).Dispose();

    /// <summary>
    /// Why <paramref name="needed"/> bytes do not fit (null when they do): the share of physical memory left after
    /// <paramref name="committedBytes"/>, capped by what the machine has available (2.2 review M1). The next step depends
    /// on the snapshot (review M11): only an in-memory snapshot with an index cache can be reopened mapped.
    /// </summary>
    internal static string? Refusal(long needed, string what, MemoryBudget budget, long committedBytes, MemoryMode graphMode, bool hasCache)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var room = Math.Min(budget.AvailableBytes, (long)(budget.TotalPhysicalBytes * budget.Fraction) - committedBytes);
        if (needed <= room)
        {
            return null;
        }

        var next = graphMode == MemoryMode.InMemory && hasCache
            ? "Close other snapshots, or open this one mapped, and try again."
            : "Close other snapshots or other applications, and try again.";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{what} needs about {needed / (1024 * 1024):N0} MB, and the analysis worker has {Math.Max(0, room) / (1024 * 1024):N0} MB left of its memory budget. {next}");
    }

    /// <summary>
    /// Returns the large arrays a computation dropped to the OS, completed, failed or canceled (ARCHITECTURE.md 9, review
    /// M3): a blocking gen2 collection that does not compact, so no live graph array is copied.
    /// </summary>
    public static void Collect() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);

    private static long CommittedBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private void Release(long bytes)
    {
        lock (_gate)
        {
            _reserved -= bytes;
        }
    }

    private sealed class Reservation(MemoryReservations owner, long bytes) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(bytes);
            }
        }
    }
}
