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
        var signatures = new Signatures(graph, tree);
        var groups = new Dictionary<int, (long Objects, ulong Bytes, int Example)>();
        long objects = 0, unreachable = 0;
        ulong unreachableBytes = 0;
        var typeIndices = graph.TypeIndices.Span;
        for (var o = 0; o < typeIndices.Length; o++)
        {
            if ((o & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!types[typeIndices[o]])
            {
                continue;
            }

            objects++;
            var size = graph.GetSize(o);
            var parent = tree.Parent(o);
            if (parent == RootPathTree.Unreachable)
            {
                unreachable++;
                unreachableBytes += size;
                continue;
            }

            // The path up to the object that references it; a rooted object has the root alone.
            var signature = parent == RootPathTree.Source ? signatures.RootOnly(o) : signatures.Of(parent);
            groups[signature] = groups.TryGetValue(signature, out var group)
                ? (group.Objects + 1, group.Bytes + size, group.Example)
                : (1, size, o);
        }

        var ordered = groups.OrderByDescending(g => g.Value.Objects).ThenByDescending(g => g.Value.Bytes).ToList();
        var paths = ordered.Take(maxPaths)
            .Select(g => new KeyPath(signatures.RootOf(g.Key), signatures.Steps(g.Key), g.Value.Objects, g.Value.Bytes, g.Value.Example))
            .ToList();
        var others = ordered.Skip(maxPaths).ToList();
        return new KeyPathSet(
            paths,
            objects,
            unreachable,
            unreachableBytes,
            others.Count,
            others.Sum(g => g.Value.Objects),
            others.Aggregate(0UL, (sum, g) => sum + g.Value.Bytes));
    }

    /// <summary>
    /// Interned path signatures: an entry is (previous entry, type index), or a root entry with its root key. A step whose
    /// type equals the previous step's keeps the previous entry, so repeats of one type fold into one step.
    /// </summary>
    private sealed class Signatures(HeapGraph graph, RootPathTree tree)
    {
        private readonly List<(int Previous, int Type, PathRoot Root)> _entries = [];
        private readonly Dictionary<(int Previous, int Type), int> _steps = [];
        private readonly Dictionary<(string RootKey, int Type), int> _roots = [];
        /// <summary>Objects whose signature is remembered at most (review M5); later ones are walked again instead.</summary>
        private const int MaxRemembered = 8_000_000;

        private readonly Dictionary<int, int> _ofObject = [];
        private readonly Stack<int> _pending = new();

        /// <summary>The signature of the path from a root to <paramref name="objectIndex"/>, the object included.</summary>
        public int Of(int objectIndex)
        {
            var pending = _pending;
            pending.Clear();
            var current = objectIndex;
            int signature;
            while (true)
            {
                if (_ofObject.TryGetValue(current, out signature))
                {
                    break;
                }

                var parent = tree.Parent(current);
                if (parent == RootPathTree.Source)
                {
                    signature = RootEntry(current, graph.TypeIndices[current]);
                    _ofObject[current] = signature;
                    break;
                }

                pending.Push(current);
                current = parent;
            }

            while (pending.Count > 0)
            {
                var next = pending.Pop();
                var type = graph.TypeIndices[next];
                if (_entries[signature].Type != type)
                {
                    if (!_steps.TryGetValue((signature, type), out var stepped))
                    {
                        stepped = _entries.Count;
                        _entries.Add((signature, type, _entries[signature].Root));
                        _steps[(signature, type)] = stepped;
                    }

                    signature = stepped;
                }

                // The object asked about is usually the one holder of one object of the set (millions of them), so only
                // its ancestors, which paths share, are remembered; asking again costs one step to a remembered parent.
                if (next != objectIndex && _ofObject.Count < MaxRemembered)
                {
                    _ofObject[next] = signature;
                }
            }

            return signature;
        }

        /// <summary>The signature of an object that is a root itself: the root with no steps.</summary>
        public int RootOnly(int objectIndex) => RootEntry(objectIndex, -1);

        public PathRoot RootOf(int signature) => _entries[signature].Root;

        /// <summary>The type indices of a signature, root first (none for a root-only signature).</summary>
        public IReadOnlyList<int> Steps(int signature)
        {
            var steps = new List<int>();
            for (var entry = signature; entry >= 0; entry = _entries[entry].Previous)
            {
                if (_entries[entry].Type >= 0)
                {
                    steps.Add(_entries[entry].Type);
                }
            }

            steps.Reverse();
            return steps;
        }

        private int RootEntry(int objectIndex, int type)
        {
            PathRoots.TryGet(graph, objectIndex, out var root);
            var key = (PathRoots.GroupKey(root, PathRoots.Detail(graph, root)), type);
            if (!_roots.TryGetValue(key, out var entry))
            {
                entry = _entries.Count;
                _entries.Add((-1, type, root));
                _roots[key] = entry;
            }

            return entry;
        }
    }
}
