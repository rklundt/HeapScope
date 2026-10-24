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

namespace HeapScope.Core;

/// <summary>
/// Reference-counted leases on a snapshot's data (ARCHITECTURE.md 6.5). A query, or a result set that pages over
/// the snapshot, holds a lease while it uses spans over the snapshot's columns. Closing never blocks: it refuses new
/// leases and runs the release action when the last lease is returned (or at once when none is held), so a mapped
/// view is never unmapped under a reader.
/// </summary>
public sealed class LeaseTracker
{
    private readonly object _gate = new();
    private int _count;
    private bool _closing;
    private Action? _onDrained;

    /// <summary>Number of leases currently held.</summary>
    public int ActiveLeases
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>True once <see cref="Close"/> has been called.</summary>
    public bool IsClosing
    {
        get
        {
            lock (_gate)
            {
                return _closing;
            }
        }
    }

    /// <summary>Takes a lease. Throws <see cref="ObjectDisposedException"/> once closing has begun.</summary>
    public SnapshotLease Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _count++;
        }

        return new SnapshotLease(this);
    }

    /// <summary>
    /// Refuses new leases and runs <paramref name="onDrained"/> exactly once: now when no lease is held, otherwise on
    /// the thread that releases the last lease. Later calls do nothing.
    /// </summary>
    public void Close(Action onDrained)
    {
        ArgumentNullException.ThrowIfNull(onDrained);
        Action? runNow = null;
        lock (_gate)
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            if (_count == 0)
            {
                runNow = onDrained;
            }
            else
            {
                _onDrained = onDrained;
            }
        }

        runNow?.Invoke();
    }

    internal void Release()
    {
        Action? drained = null;
        lock (_gate)
        {
            _count--;
            if (_count == 0 && _closing)
            {
                drained = _onDrained;
                _onDrained = null;
            }
        }

        drained?.Invoke();
    }
}

/// <summary>A held lease; dispose it when the work is done. Disposing twice releases once.</summary>
public sealed class SnapshotLease : IDisposable
{
    private LeaseTracker? _tracker;

    internal SnapshotLease(LeaseTracker tracker) => _tracker = tracker;

    public void Dispose() => Interlocked.Exchange(ref _tracker, null)?.Release();
}
