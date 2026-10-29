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
/// One key retention path of a set of objects (sprint 2.2, dotMemory's Key Retention Paths): the objects whose shortest path
/// from a root has the same type sequence (<see cref="Steps"/>, root first, the objects themselves excluded; repeats of one
/// type folded) and the same kind of root (and field, for statics).
/// </summary>
/// <param name="Root">The root of the example object's path.</param>
/// <param name="Steps">Type indices from the root's object to the object that references the set's object; empty when
/// the objects are roots themselves.</param>
/// <param name="ExampleObject">One of the objects, to open its own paths.</param>
public sealed record KeyPath(PathRoot Root, IReadOnlyList<int> Steps, long Objects, ulong Bytes, int ExampleObject);

/// <summary>A set's key paths, largest first, and what they do not cover.</summary>
/// <param name="Objects">Objects in the set.</param>
/// <param name="Unreachable">Objects no root reaches: in a full dump garbage not yet collected or held only weakly, in a
/// gcdump objects whose root was not recorded (review M3).</param>
/// <param name="OtherPaths">Key paths beyond the ones returned; <see cref="OtherObjects"/> and <see cref="OtherBytes"/> sum them.</param>
public sealed record KeyPathSet(
    IReadOnlyList<KeyPath> Paths,
    long Objects,
    long Unreachable,
    ulong UnreachableBytes,
    int OtherPaths,
    long OtherObjects,
    ulong OtherBytes);

/// <summary>Groups a set of objects by their shortest path from a root (sprint 2.2).</summary>
public static class KeyPaths
{
    /// <summary>Key paths returned by default (decision 3).</summary>
    public const int DefaultMaxPaths = 20;

    /// <summary>
    /// The key paths of every object whose type is set in <paramref name="types"/>. A path's signature is built once per
    /// object on the way (memoized), so the work is linear in the objects the paths pass through, even for long lists.
    /// </summary>
    public static KeyPathSet ForTypes(HeapGraph graph, RootPathTree tree, IReadOnlyList<bool> types, int maxPaths = DefaultMaxPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(types);
        var groups = new Grouping(graph, tree);
        var typeIndices = graph.TypeIndices.Span;
        for (var o = 0; o < typeIndices.Length; o++)
        {
            if ((o & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (types[typeIndices[o]])
            {
                groups.Add(o);
            }
        }

        return groups.Result(maxPaths);
    }

    /// <summary>The key paths of the objects <paramref name="members"/> (sprint 3.2: a set's members).</summary>
    public static KeyPathSet ForMembers(HeapGraph graph, RootPathTree tree, ReadOnlySpan<int> members, int maxPaths = DefaultMaxPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(tree);
        var groups = new Grouping(graph, tree);
        for (var i = 0; i < members.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            groups.Add(members[i]);
        }

        return groups.Result(maxPaths);
    }

    /// <summary>Objects grouped by the signature of their shortest path.</summary>
    private sealed class Grouping(HeapGraph graph, RootPathTree tree)
    {
        private readonly PathSignatures _signatures = new(graph, tree);
        private readonly Dictionary<int, (long Objects, ulong Bytes, int Example)> _groups = [];
        private long _objects;
        private long _unreachable;
        private ulong _unreachableBytes;

        public void Add(int o)
        {
            _objects++;
            var size = graph.GetSize(o);
            if (!_signatures.TryOfObject(o, out var signature))
            {
                _unreachable++;
                _unreachableBytes += size;
                return;
            }

            _groups[signature] = _groups.TryGetValue(signature, out var group)
                ? (group.Objects + 1, group.Bytes + size, group.Example)
                : (1, size, o);
        }

        public KeyPathSet Result(int maxPaths)
        {
            var ordered = _groups.OrderByDescending(g => g.Value.Objects).ThenByDescending(g => g.Value.Bytes).ToList();
            var paths = ordered.Take(maxPaths)
                .Select(g => new KeyPath(_signatures.RootOf(g.Key), _signatures.Steps(g.Key), g.Value.Objects, g.Value.Bytes, g.Value.Example))
                .ToList();
            var others = ordered.Skip(maxPaths).ToList();
            return new KeyPathSet(
                paths,
                _objects,
                _unreachable,
                _unreachableBytes,
                others.Count,
                others.Sum(g => g.Value.Objects),
                others.Aggregate(0UL, (sum, g) => sum + g.Value.Bytes));
        }
    }
}
