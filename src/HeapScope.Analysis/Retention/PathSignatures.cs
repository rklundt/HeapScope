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
/// Interned path signatures: an entry is (previous entry, type index), or a root entry with its root key. A step whose
/// type equals the previous step's keeps the previous entry, so repeats of one type fold into one step.
/// </summary>
internal sealed class PathSignatures(HeapGraph graph, RootPathTree tree)
{
    private readonly List<(int Previous, int Type, PathRoot Root, string RootKey)> _entries = [];
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
                    _entries.Add((signature, type, _entries[signature].Root, _entries[signature].RootKey));
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

    /// <summary>
    /// The signature of the path that keeps <paramref name="objectIndex"/> alive: the path up to the object that references
    /// it, or the root alone when it is a root. False when no root reaches it.
    /// </summary>
    public bool TryOfObject(int objectIndex, out int signature)
    {
        var parent = tree.Parent(objectIndex);
        if (parent == RootPathTree.Unreachable)
        {
            signature = -1;
            return false;
        }

        signature = parent == RootPathTree.Source ? RootOnly(objectIndex) : Of(parent);
        return true;
    }

    /// <summary>
    /// Steps two signatures share from the root on (0 when their roots differ): how alike two paths are (sprint 3.2,
    /// the most different paths of a Similar Retention group).
    /// </summary>
    public int SharedSteps(int first, int second)
    {
        var a = Steps(first);
        var b = Steps(second);
        if (!string.Equals(_entries[first].RootKey, _entries[second].RootKey, StringComparison.Ordinal))
        {
            return 0;
        }

        var shared = 1;
        for (var i = 0; i < Math.Min(a.Count, b.Count) && a[i] == b[i]; i++)
        {
            shared++;
        }

        return shared;
    }

    private int RootEntry(int objectIndex, int type)
    {
        PathRoots.TryGet(graph, objectIndex, out var root);
        var key = (PathRoots.GroupKey(root, PathRoots.Detail(graph, root)), type);
        if (!_roots.TryGetValue(key, out var entry))
        {
            entry = _entries.Count;
            _entries.Add((-1, type, root, key.Item1));
            _roots[key] = entry;
        }

        return entry;
    }
}
