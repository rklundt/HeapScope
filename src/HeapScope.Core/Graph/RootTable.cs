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

namespace HeapScope.Core.Graph;

/// <summary>GC roots sorted by object index, then kind (ARCHITECTURE.md 6.1).</summary>
public sealed class RootTable
{
    public RootTable(Column<GcRoot> roots)
    {
        Roots = roots;
    }

    public static RootTable Empty { get; } = new(Column<GcRoot>.Empty);

    public Column<GcRoot> Roots { get; }

    public int Count => Roots.Length;

    /// <summary>The roots of one object (empty when it has none).</summary>
    public ReadOnlySpan<GcRoot> GetRoots(int objectIndex)
    {
        var roots = Roots.Span;
        var lo = LowerBound(roots, objectIndex);
        var hi = lo;
        while (hi < roots.Length && roots[hi].ObjectIndex == objectIndex)
        {
            hi++;
        }

        return roots[lo..hi];
    }

    /// <summary>True when the object has at least one retaining root.</summary>
    public bool IsRetained(int objectIndex)
    {
        foreach (var root in GetRoots(objectIndex))
        {
            if (root.IsRetaining)
            {
                return true;
            }
        }

        return false;
    }

    private static int LowerBound(ReadOnlySpan<GcRoot> roots, int objectIndex)
    {
        int lo = 0, hi = roots.Length;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (roots[mid].ObjectIndex < objectIndex)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
