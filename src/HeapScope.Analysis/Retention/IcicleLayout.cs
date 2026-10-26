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
using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Retention;

/// <summary>
/// The icicle chart of the dominator tree (sprint 2.4): the focus (or the whole reachable heap) as the top bar, each
/// object's dominator children below it, side by side in retained order, as wide as their retained bytes. Siblings retain
/// disjoint sets, so children always fit inside their parent; the gap at a parent's right is its own bytes and the
/// children too small to draw. A segment narrower than <c>minShare</c> of the top bar is left out with its subtree.
/// </summary>
public static class IcicleLayout
{
    /// <summary>The most segments one chart holds (a share of 0.1 percent allows at most 1,000 per level).</summary>
    public const int MaxSegments = 20_000;

    /// <summary>
    /// The segments of <paramref name="levels"/> levels below <paramref name="focus"/> (an object index; -1 for the virtual
    /// root), level 0 being the focus itself, in level order and left to right within a level.
    /// </summary>
    public static IReadOnlyList<IcicleSegment> Build(HeapGraph graph, Dominators dominators, int focus, int levels, double minShare, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(dominators);
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 1);
        if (focus < -1 || focus >= graph.ObjectCount)
        {
            throw new ArgumentOutOfRangeException(nameof(focus));
        }

        if (minShare is < 0 or > 1 || double.IsNaN(minShare))
        {
            throw new ArgumentOutOfRangeException(nameof(minShare));
        }

        var top = focus >= 0 ? focus : dominators.Root;
        var total = focus >= 0 ? dominators.RetainedSize(focus) : dominators.ReachableBytes();
        var minBytes = (ulong)Math.Ceiling(total * minShare);
        var segments = new List<IcicleSegment> { new(0, 0, total, focus) };
        var level = new List<(int Vertex, ulong Offset)> { (top, 0) };
        for (var depth = 1; depth <= levels && level.Count > 0; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = new List<(int Vertex, ulong Offset)>();
            foreach (var (vertex, offset) in level)
            {
                var x = offset;
                var wide = new List<int>();
                foreach (var child in dominators.ChildrenOf(vertex))
                {
                    var width = dominators.RetainedSize(child);
                    if (width >= minBytes && width > 0)
                    {
                        wide.Add(child);
                    }
                }

                foreach (var child in RetainedOrder.Top(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(wide), Math.Min(wide.Count, MaxSegments - segments.Count), dominators))
                {
                    var width = dominators.RetainedSize(child);
                    segments.Add(new IcicleSegment(depth, x, width, child));
                    next.Add((child, x));
                    x += width;
                }
            }

            level = next;
        }

        return segments;
    }
}

/// <summary>A bar of the icicle chart: its level (0 at the top), its left edge and width in bytes, and its object (-1 for the whole heap).</summary>
public readonly record struct IcicleSegment(int Level, ulong Offset, ulong Width, int Object);
