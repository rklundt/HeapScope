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


namespace HeapScope.Analysis.Inspections;

/// <summary>
/// Objects, bytes and members per type, counted the same way by every detector that reports by type (sprint 3.8, peer
/// review P19). Members keep the order they were added in; each detector orders its rows itself.
/// </summary>
internal sealed class TypeTally
{
    private readonly Dictionary<int, Entry> _types = [];

    /// <summary>The number of types counted.</summary>
    public int Count => _types.Count;

    /// <summary>The types counted, with their objects, bytes and members.</summary>
    public IEnumerable<(int Type, long Objects, long Bytes, List<int> Members)> Types =>
        _types.Select(t => (t.Key, (long)t.Value.Members.Count, t.Value.Bytes, t.Value.Members));

    /// <summary>Counts object <paramref name="obj"/> of <paramref name="type"/>, <paramref name="size"/> bytes.</summary>
    public void Add(int type, int obj, ulong size)
    {
        if (!_types.TryGetValue(type, out var entry))
        {
            _types[type] = entry = new Entry();
        }

        entry.Members.Add(obj);
        entry.Bytes += (long)size;
    }

    private sealed class Entry
    {
        public List<int> Members { get; } = [];

        public long Bytes { get; set; }
    }
}
