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

using System.Globalization;
using HeapScope.Analysis.Retention;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// Retention results on the wire (sprint 2.2): roots in plain language, fields named from the dump (full dumps, within the
/// referrers' element budget), steps through dependent handles marked, framework steps flagged (decision 4).
/// </summary>
internal static class RetentionMapping
{
    /// <summary>Steps whose holding field is looked up at most per query (a path can be a long list).</summary>
    public const int MaxNamedSteps = 1_000;

    /// <summary>Segments kept per path; a longer path keeps its two ends around one gap node (review H1).</summary>
    public const int MaxPathSegments = 64;

    /// <summary>Nodes of one object's tree at most; roots whose paths would pass it are left out (review H1).</summary>
    public const int MaxNodes = 2_000;

    /// <summary>Steps of a key path at most; a longer one keeps its ends around one "(N steps not shown)" step.</summary>
    public const int MaxKeyPathSteps = 64;

    /// <summary>A key path's steps, cut to <see cref="MaxKeyPathSteps"/> around one step that says how many are hidden.</summary>
    public static List<PathStepDto> CapSteps(List<PathStepDto> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count <= MaxKeyPathSteps)
        {
            return steps;
        }

        var keepNear = MaxKeyPathSteps / 2;
        var keepFar = MaxKeyPathSteps - keepNear - 1;
        var hidden = steps.Count - keepNear - keepFar;
        return [.. steps.Take(keepNear), new PathStepDto(string.Create(CultureInfo.InvariantCulture, $"({hidden:N0} steps not shown)"), false), .. steps.TakeLast(keepFar)];
    }

    /// <summary>Whether a type is a framework type a front end may fold when it lies between other steps.</summary>
    public static bool IsFrameworkType(HeapGraph graph, int type) =>
        HeapScope.Analysis.FrameworkTypes.IsFrameworkStep(graph.Types.GetName(type), graph.Types.GetModule(type));

    /// <summary>What "no root reaches it" means for a snapshot of <paramref name="kind"/>, as its kind handler words it.</summary>
    public static string UnreachableNote(SnapshotKind kind) => Snapshots.SnapshotKinds.Describe(kind).UnreachableNote;

    public static PathRootDto Root(HeapGraph graph, PathRoot root) =>
        new(root.IsFrozen ? RootKindDto.Other : root.Root.Kind.ToDto(), root.IsFrozen, PathRoots.Describe(root, PathRoots.Detail(graph, root)));

    public static KeyPathDto KeyPath(HeapGraph graph, KeyPath path)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(path);
        var steps = path.Steps.Select((type, i) =>
        {
            var name = graph.Types.GetName(type);
            return new PathStepDto(name, i > 0 && IsFrameworkType(graph, type));
        });
        return new KeyPathDto(
            Root(graph, path.Root),
            CapSteps([.. steps]),
            graph.Types.GetName(graph.TypeIndices[path.ExampleObject]),
            path.Objects,
            (long)path.Bytes,
            graph.Addresses[path.ExampleObject]);
    }

    public static ObjectPathsResult ObjectPaths(
        HeapGraph graph,
        RetentionTree tree,
        IObjectReader? reader,
        string? noReaderNote,
        bool completeGraph,
        ReverseIndexInfo index,
        CancellationToken cancellationToken,
        SnapshotKind kind = SnapshotKind.FullDump)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(tree);
        var builder = new TreeBuilder(graph, tree, reader, cancellationToken);
        var rootNodeIds = new List<int>();
        var truncated = false;
        foreach (var (node, root) in tree.Roots)
        {
            if (builder.Add(node, root) is { } id)
            {
                rootNodeIds.Add(id);
            }
            else
            {
                truncated = true;
                break;
            }
        }

        return new ObjectPathsResult(
            builder.Nodes(),
            rootNodeIds,
            KeyPathsOf(graph, tree),
            tree.IsReachable,
            tree.StoppedEarly || truncated,
            tree.Visited,
            reader is null && tree.Nodes.Count > 1 ? noReaderNote : builder.FieldsNote,
            completeGraph,
            index,
            truncated,
            UnreachableNote(kind));
    }

    /// <summary>The roots found, grouped like a type's key paths: same root kind (and field) and same folded type sequence.</summary>
    private static List<KeyPathDto> KeyPathsOf(HeapGraph graph, RetentionTree tree)
    {
        var groups = new Dictionary<string, (KeyPathDto Path, long Count)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (node, root) in tree.Roots)
        {
            var steps = new List<PathStepDto>();
            var path = tree.PathOf(node);
            for (var i = 0; i < path.Count - 1; i++)
            {
                var type = graph.TypeIndices[tree.Nodes[path[i]].ObjectIndex];
                var name = graph.Types.GetName(type);
                if (steps.Count == 0 || steps[^1].TypeName != name)
                {
                    steps.Add(new PathStepDto(name, steps.Count > 0 && IsFrameworkType(graph, type)));
                }
            }

            steps = CapSteps(steps);
            var rootDto = Root(graph, root);
            var key = PathRoots.GroupKey(root, PathRoots.Detail(graph, root)) + "|" + string.Join(">", steps.Select(s => s.TypeName));
            if (groups.TryGetValue(key, out var group))
            {
                groups[key] = (group.Path, group.Count + 1);
                continue;
            }

            var target = tree.Nodes[0].ObjectIndex;
            groups[key] = (new KeyPathDto(rootDto, steps, graph.Types.GetName(graph.TypeIndices[target]), 1, (long)graph.GetSize(target), graph.Addresses[root.ObjectIndex]), 1);
            order.Add(key);
        }

        return [.. order.Select(k => groups[k].Path with { Objects = groups[k].Count })];
    }

    /// <summary>
    /// Builds the wire tree of an object's paths (review H1), root by root: each path, from the object outward, is cut into
    /// segments where a run of objects of one type (a linked list, nested nodes) is one segment with a folded count; a path
    /// of more than <see cref="MaxPathSegments"/> segments keeps its two ends around one gap node; segments shared with an
    /// earlier path merge into its nodes. Stops before passing <see cref="MaxNodes"/> nodes. Fields are named per segment
    /// on the edge it shows, within the step and array element budgets.
    /// </summary>
    private sealed class TreeBuilder(HeapGraph graph, RetentionTree tree, IObjectReader? reader, CancellationToken cancellationToken)
    {
        private readonly List<Draft> _nodes = [new Draft(tree.Nodes[0].ObjectIndex, -1, null, 0, false, 0, null, 0)];
        // Segments merge only when they are the same objects (first, last and count), so two paths that branch inside a run
        // of one type show the shared object twice rather than a holder on the wrong object (H1 confirmation).
        private readonly Dictionary<(int Child, int First, int Last, int Count), int> _children = [];
        private long _elementBudget = ReferrersSource.MaxNamedElementsPerQuery;
        private int _named;

        public string? FieldsNote { get; private set; }

        /// <summary>Adds one root's path; returns the root's node id, or null when it would pass the node limit.</summary>
        public int? Add(int rootNode, PathRoot root)
        {
            // The path from the object outward, then cut into same-type segments (the object and the root alone).
            var objects = tree.PathOf(rootNode).Select(n => tree.Nodes[n].ObjectIndex).Reverse().ToList();
            var segments = new List<(int First, int Last, int Count)> { (objects[0], objects[0], 1) };
            for (var i = 1; i < objects.Count; i++)
            {
                var o = objects[i];
                var (first, last, count) = segments[^1];
                var isRoot = i == objects.Count - 1;
                if (segments.Count > 1 && !isRoot && graph.TypeIndices[o] == graph.TypeIndices[first])
                {
                    segments[^1] = (first, o, count + 1);
                }
                else
                {
                    segments.Add((o, o, 1));
                }
            }

            // A long path keeps its ends around one gap.
            var hidden = 0;
            if (segments.Count > MaxPathSegments)
            {
                var keepNear = MaxPathSegments / 2;
                var keepFar = MaxPathSegments - keepNear - 1;
                hidden = segments.Skip(keepNear).Take(segments.Count - keepNear - keepFar).Sum(s => s.Count);
                var gapLast = segments[segments.Count - keepFar - 1].Last;
                segments = [.. segments.Take(keepNear), (-1, gapLast, hidden), .. segments.TakeLast(keepFar)];
            }

            // Counts the path's segments even where they will merge with earlier nodes: the limit errs on the safe side.
            if (_nodes.Count + segments.Count > MaxNodes)
            {
                return null;
            }

            var child = 0;
            _nodes[0] = _nodes[0] with { RootsBelow = _nodes[0].RootsBelow + 1 };
            if (segments.Count == 1)
            {
                // A root holds the object itself: its path is the object alone.
                _nodes[0] = _nodes[0] with { Root = root };
                return 0;
            }

            var previousLast = segments[0].Last;
            for (var s = 1; s < segments.Count; s++)
            {
                var (first, last, count) = segments[s];
                var key = (child, first >= 0 ? first : -1 - rootNode, last, count);
                if (!_children.TryGetValue(key, out var id))
                {
                    id = _nodes.Count;
                    var isRoot = s == segments.Count - 1;
                    var field = first >= 0 && previousLast >= 0 ? NameField(first, previousLast) : null;
                    var dependent = first >= 0 && previousLast >= 0 && field is null && graph.IsDependentHandleEdge(first, previousLast);
                    _nodes.Add(new Draft(first, child, field, s, dependent, first < 0 ? count : count - 1, isRoot ? root : null, 0));
                    _children[key] = id;
                }

                _nodes[id] = _nodes[id] with { RootsBelow = _nodes[id].RootsBelow + 1 };
                child = id;
                previousLast = last;
            }

            return child;
        }

        public List<RetentionNodeDto> Nodes() =>
        [
            .. _nodes.Select((d, id) =>
            {
                if (d.ObjectIndex < 0)
                {
                    var text = string.Create(CultureInfo.InvariantCulture, $"({d.Folded:N0} objects not shown)");
                    return new RetentionNodeDto(id, d.Child, 0, text, null, false, false, d.Depth, null, d.RootsBelow, d.Folded);
                }

                var typeName = graph.Types.GetName(graph.TypeIndices[d.ObjectIndex]);
                return new RetentionNodeDto(
                    id,
                    d.Child,
                    graph.Addresses[d.ObjectIndex],
                    typeName,
                    d.Field,
                    d.Dependent,
                    id != 0 && d.Root is null && IsFrameworkType(graph, graph.TypeIndices[d.ObjectIndex]),
                    d.Depth,
                    d.Root is { } root ? Root(graph, root) : null,
                    d.RootsBelow,
                    d.Folded);
            }),
        ];

        /// <summary>The field of <paramref name="holder"/> that holds <paramref name="child"/>, read within the budgets.</summary>
        private string? NameField(int holder, int child)
        {
            if (reader is null || FieldsNote is not null)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var walked = Math.Min(ReferrersSource.MaxNamedElements, ReferrersSource.ArrayLength(graph, holder));
            if (_named >= MaxNamedSteps || walked > _elementBudget)
            {
                FieldsNote = string.Create(CultureInfo.InvariantCulture, $"Fields are named for the first {_named:N0} steps: the paths are long or pass through large arrays. Open an object on the path to see its fields.");
                return null;
            }

            _elementBudget -= walked;
            _named++;
            var paths = reader.ReadReferencePaths(graph.Addresses[holder], ReferrersSource.MaxNamedElements, cancellationToken);
            var field = paths.GetValueOrDefault(graph.Addresses[child]);
            return string.IsNullOrEmpty(field) ? null : field;
        }

        /// <summary>A node before it goes on the wire; <see cref="ObjectIndex"/> is -1 for a gap.</summary>
        private sealed record Draft(int ObjectIndex, int Child, string? Field, int Depth, bool Dependent, int Folded, PathRoot? Root, int RootsBelow);
    }
}
