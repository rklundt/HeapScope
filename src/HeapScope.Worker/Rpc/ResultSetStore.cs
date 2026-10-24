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

using HeapScope.Contracts;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// Result sets held by the worker for paged retrieval (ARCHITECTURE.md 4.6). Sets are typed sources, formatted
/// per page. Least recently used sets are evicted when either the set count or the estimated byte budget is
/// exceeded, so a client that never releases cannot grow the worker without bound. A set may carry an owner
/// (for example a snapshot lease) that is disposed when the set is released or evicted, or, when a page is being
/// formatted at that moment, after that page: sources read the graph and the dump while formatting (sprint 1.5 review).
/// </summary>
internal sealed class ResultSetStore
{
    public const int DefaultCapacity = 32;
    public const long DefaultByteBudget = 256L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, LinkedListNode<StoredResultSet>> _sets = [];
    private readonly LinkedList<StoredResultSet> _recency = new();
    private long _bytes;

    public ResultSetStore(int capacity = DefaultCapacity, long byteBudget = DefaultByteBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(byteBudget, 1);
        Capacity = capacity;
        ByteBudget = byteBudget;
    }

    public int Capacity { get; }

    public long ByteBudget { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sets.Count;
            }
        }
    }

    public long EstimatedBytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>
    /// Stores a source and returns the descriptor clients page through. The newest set is always kept, even when it
    /// alone exceeds the byte budget; older sets are evicted first.
    /// </summary>
    public ResultSetInfo Add(IResultSource source, IDisposable? owner = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var set = new StoredResultSet(Guid.NewGuid(), source, owner);
        var evicted = new List<StoredResultSet>();
        lock (_gate)
        {
            _sets.Add(set.Id, _recency.AddFirst(set));
            _bytes += source.EstimatedBytes;
            while (_sets.Count > 1 && (_sets.Count > Capacity || _bytes > ByteBudget))
            {
                var oldest = _recency.Last!.Value;
                RemoveLocked(oldest.Id);
                evicted.Add(oldest);
            }
        }

        foreach (var old in evicted)
        {
            old.DisposeOwnerIfIdle();
        }

        return new ResultSetInfo(set.Id, source.Count, source.Columns);
    }

    /// <summary>Returns a page (at most <see cref="WorkerProtocol.MaxPageSize"/> rows), or null when the set is gone.</summary>
    public ResultPage? TryGetPage(Guid id, int offset, int count)
    {
        StoredResultSet set;
        lock (_gate)
        {
            if (!_sets.TryGetValue(id, out var node))
            {
                return null;
            }

            _recency.Remove(node);
            _recency.AddFirst(node);
            set = node.Value;
            set.Readers++;
        }

        try
        {
            return FormatPage(set, offset, count);
        }
        finally
        {
            bool last;
            lock (_gate)
            {
                last = --set.Readers == 0 && set.Removed;
            }

            if (last)
            {
                set.Owner?.Dispose();
            }
        }
    }

    public void Release(Guid id)
    {
        StoredResultSet? removed;
        lock (_gate)
        {
            removed = RemoveLocked(id);
        }

        removed?.DisposeOwnerIfIdle();
    }

    private static ResultPage FormatPage(StoredResultSet set, int offset, int count)
    {
        var total = set.Source.Count;
        var start = Math.Min(offset, total);
        var length = Math.Min(Math.Min(count, WorkerProtocol.MaxPageSize), total - start);
        var columns = set.Source.Columns.Count;
        var rows = new IReadOnlyList<string?>[length];
        for (var i = 0; i < length; i++)
        {
            var cells = new string?[columns];
            set.Source.FormatRow(start + i, cells);
            rows[i] = cells;
        }

        return new ResultPage(set.Id, start, total, rows);
    }

    private StoredResultSet? RemoveLocked(Guid id)
    {
        if (!_sets.Remove(id, out var node))
        {
            return null;
        }

        _recency.Remove(node);
        _bytes -= node.Value.Source.EstimatedBytes;
        node.Value.Removed = true;
        node.Value.IdleWhenRemoved = node.Value.Readers == 0;
        return node.Value;
    }

    /// <summary>A stored set and the pages being formatted from it (<see cref="Readers"/>, guarded by the store's lock).</summary>
    private sealed class StoredResultSet(Guid id, IResultSource source, IDisposable? owner)
    {
        public Guid Id { get; } = id;

        public IResultSource Source { get; } = source;

        public IDisposable? Owner { get; } = owner;

        public int Readers { get; set; }

        public bool Removed { get; set; }

        /// <summary>No page was being formatted when the set was removed: the remover disposes the owner, else the last reader does.</summary>
        public bool IdleWhenRemoved { get; set; }

        public void DisposeOwnerIfIdle()
        {
            if (IdleWhenRemoved)
            {
                Owner?.Dispose();
            }
        }
    }
}
