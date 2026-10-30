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


using HeapScope.Analysis.Inspections;
using HeapScope.Analysis.Sets;

namespace HeapScope.Worker.Snapshots;

/// <summary>
/// Resolved object sets and the leak detector tables their rows come from (sprint 3.1, ADR-030), kept per snapshot so the
/// views of one set (types, instances, generations, dominators, retained) resolve it once. Least recently used entries
/// go first beyond <see cref="MaxSets"/> sets or <see cref="MaxBytes"/> of them, and beyond <see cref="MaxTableBytes"/> of
/// tables; a set larger than half the budget is not kept. The snapshot store owns it: closing a snapshot forgets its
/// entries, and an entry for a snapshot that is no longer open (a resolution that finished after the close) is dropped.
/// </summary>
/// <param name="isOpen">
/// Whether a snapshot is still open, read under the cache's lock: a close removes the snapshot, then calls
/// <see cref="Forget"/>, so an entry added meanwhile is either dropped by it or refused.
/// </param>
internal sealed class ObjectSetCache(Func<Guid, bool> isOpen)
{
    /// <summary>Sets kept over all snapshots.</summary>
    public const int MaxSets = 8;

    /// <summary>Bytes of sets kept over all snapshots.</summary>
    public const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>Bytes of inspection tables (rows and member lists) kept over all snapshots.</summary>
    public const long MaxTableBytes = 128L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly LinkedList<(Guid Snapshot, string Key, ObjectSet Set)> _sets = new();
    private readonly LinkedList<(Guid Snapshot, string Key, InspectionTable Table)> _tables = new();

    /// <summary>Entries dropped to stay within the budgets, for the log.</summary>
    public event Action<string>? Evicted;

    public ObjectSet? TryGet(Guid snapshotId, string key)
    {
        lock (_gate)
        {
            var node = Find(_sets, e => e.Snapshot == snapshotId && e.Key == key);
            return node?.Value.Set;
        }
    }

    /// <summary>
    /// Keeps <paramref name="set"/> (replacing an entry of the same key) unless it alone is over half the budget, or its
    /// snapshot, or a snapshot its key names (a compared set's or an inspection row's baseline), is no longer open.
    /// </summary>
    public void Add(Guid snapshotId, string key, ObjectSet set)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(set);
        if (set.EstimatedBytes > MaxBytes / 2)
        {
            return;
        }

        lock (_gate)
        {
            if (!isOpen(snapshotId) || NamesClosed(key))
            {
                return;
            }

            Remove(_sets, e => e.Snapshot == snapshotId && e.Key == key);
            _sets.AddFirst((snapshotId, key, set));
            var bytes = _sets.Sum(s => s.Set.EstimatedBytes);
            while (_sets.Count > MaxSets || bytes > MaxBytes)
            {
                bytes -= _sets.Last!.Value.Set.EstimatedBytes;
                Evicted?.Invoke($"set {_sets.Last.Value.Set.Count} objects of snapshot {_sets.Last.Value.Snapshot}");
                _sets.RemoveLast();
            }
        }
    }

    /// <summary>A detector's rows, keyed by inspection and baseline, as the last run of it on the snapshot answered.</summary>
    public InspectionTable? TryGetTable(Guid snapshotId, string inspection, Guid? baseline)
    {
        lock (_gate)
        {
            var key = TableKey(inspection, baseline);
            var node = Find(_tables, e => e.Snapshot == snapshotId && e.Key == key);
            return node?.Value.Table;
        }
    }

    /// <summary>Keeps a detector's rows unless they are over half the budget, or the snapshot or its baseline is no longer open.</summary>
    public void AddTable(Guid snapshotId, string inspection, Guid? baseline, InspectionTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var key = TableKey(inspection, baseline);
        if (table.EstimatedBytes > MaxTableBytes / 2)
        {
            return;
        }

        lock (_gate)
        {
            if (!isOpen(snapshotId) || (baseline is { } other && !isOpen(other)))
            {
                return;
            }

            Remove(_tables, e => e.Snapshot == snapshotId && e.Key == key);
            _tables.AddFirst((snapshotId, key, table));
            var bytes = _tables.Sum(t => t.Table.EstimatedBytes);
            while (bytes > MaxTableBytes)
            {
                bytes -= _tables.Last!.Value.Table.EstimatedBytes;
                Evicted?.Invoke($"inspection table {_tables.Last.Value.Key} of snapshot {_tables.Last.Value.Snapshot}");
                _tables.RemoveLast();
            }
        }
    }

    /// <summary>Drops a closed snapshot's sets and tables, and those that used it as a baseline.</summary>
    public void Forget(Guid snapshotId)
    {
        lock (_gate)
        {
            Remove(_sets, e => e.Snapshot == snapshotId || e.Key.Contains(snapshotId.ToString(), StringComparison.Ordinal));
            Remove(_tables, e => e.Snapshot == snapshotId || e.Key.Contains(snapshotId.ToString(), StringComparison.Ordinal));
        }
    }

    /// <summary>Whether a part of <paramref name="key"/> names a snapshot that is not open (the ids <see cref="Forget"/> matches).</summary>
    private bool NamesClosed(string key)
    {
        foreach (var part in key.Split('|'))
        {
            if (Guid.TryParseExact(part, "D", out var named) && !isOpen(named))
            {
                return true;
            }
        }

        return false;
    }

    private static string TableKey(string inspection, Guid? baseline) => inspection.ToLowerInvariant() + "|" + baseline;

    /// <summary>The first entry that matches, moved to the front (most recently used).</summary>
    private static LinkedListNode<T>? Find<T>(LinkedList<T> list, Func<T, bool> match)
    {
        for (var node = list.First; node is not null; node = node.Next)
        {
            if (match(node.Value))
            {
                list.Remove(node);
                list.AddFirst(node);
                return node;
            }
        }

        return null;
    }

    private static void Remove<T>(LinkedList<T> list, Func<T, bool> match)
    {
        for (var node = list.First; node is not null;)
        {
            var next = node.Next;
            if (match(node.Value))
            {
                list.Remove(node);
            }

            node = next;
        }
    }
}
