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

namespace HeapScope.Analysis.Retention;

/// <summary>
/// An int stack in fixed chunks below the large object heap's threshold; emptied chunks are dropped, but one is kept
/// spare, so a walk that sits on a chunk boundary does not allocate on every push. Shared by the walks over the graph
/// (set retention, reachability).
/// </summary>
internal sealed class ChunkedStack
{
    private const int ChunkSize = 16 * 1024;

    private readonly List<int[]> _chunks = [];
    private int[]? _spare;
    private int _top; // Items in the last chunk.

    public void Push(int value)
    {
        if (_chunks.Count == 0 || _top == ChunkSize)
        {
            _chunks.Add(_spare ?? new int[ChunkSize]);
            _spare = null;
            _top = 0;
        }

        _chunks[^1][_top++] = value;
    }

    public bool TryPop(out int value)
    {
        if (_chunks.Count == 0)
        {
            value = 0;
            return false;
        }

        value = _chunks[^1][--_top];
        if (_top == 0)
        {
            _spare = _chunks[^1];
            _chunks.RemoveAt(_chunks.Count - 1);
            _top = ChunkSize;
        }

        return true;
    }
}
