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
using HeapScope.Contracts;

namespace HeapScope.UI.ViewModels;

/// <summary>What a node of the key paths diagram stands for.</summary>
public enum DiagramNodeKind
{
    /// <summary>Where paths start: a root, by its description.</summary>
    Root = 0,

    /// <summary>A type on the way, merged across paths.</summary>
    Type = 1,

    /// <summary>The objects the paths explain (the set's or the type's), at the bottom.</summary>
    Target = 2,
}

/// <summary>A node of the diagram at its place: layer (top to bottom) and column, and its rectangle.</summary>
/// <param name="Objects">The objects of the paths that pass through it.</param>
public sealed record DiagramNode(int Id, DiagramNodeKind Kind, string Label, long Objects, int Layer, int Column, double X, double Y);

/// <summary>
/// An edge of the diagram: the objects of the paths that take it, the framework types folded on it (when they are hidden),
/// and whether it goes back up (a type met earlier on another path).
/// </summary>
public sealed record DiagramEdge(int From, int To, long Objects, int Folded, bool IsBack);

/// <summary>
/// Key retention paths merged into one graph (sprint 3.2, decision 5; dotMemory's Key Retention Paths diagram): a node
/// per root and per type, the target at the bottom, and an edge per consecutive pair carrying the objects of the paths
/// through it. A node's layer is its first depth on any path, and columns follow the paths' order (the largest first),
/// so the biggest path runs down the left. Independent of the UI toolkit.
/// </summary>
public sealed class KeyPathDiagram
{
    public const double NodeWidth = 230;

    public const double NodeHeight = 46;

    public const double ColumnGap = 30;

    public const double LayerGap = 54;

    private KeyPathDiagram(IReadOnlyList<DiagramNode> nodes, IReadOnlyList<DiagramEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
        Width = nodes.Count == 0 ? 0 : ((nodes.Max(n => n.Column) + 1) * (NodeWidth + ColumnGap)) - ColumnGap;
        Height = nodes.Count == 0 ? 0 : ((nodes.Max(n => n.Layer) + 1) * (NodeHeight + LayerGap)) - LayerGap;
        MaxEdgeObjects = edges.Count == 0 ? 0 : edges.Max(e => e.Objects);
    }

    public IReadOnlyList<DiagramNode> Nodes { get; }

    public IReadOnlyList<DiagramEdge> Edges { get; }

    public double Width { get; }

    public double Height { get; }

    /// <summary>The objects of the busiest edge (edges are drawn thicker by their share of it).</summary>
    public long MaxEdgeObjects { get; }

    public static KeyPathDiagram Empty { get; } = new([], []);

    /// <summary>
    /// The diagram of <paramref name="paths"/>; framework steps between others are folded into the edges unless shown.
    /// <paramref name="overlapping"/>: every object has every path (a Similar Retention group), so a node or an edge
    /// holds the most objects of any path through it, never their sum (the 3.2 review). <paramref name="target"/> names the
    /// bottom node (a set's title) instead of each path's example type.
    /// </summary>
    public static KeyPathDiagram Build(IReadOnlyList<KeyPathDto> paths, bool showFramework, bool overlapping = false, string? target = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var labels = new List<(DiagramNodeKind Kind, string Label)>();
        var layers = new List<int>();
        var objects = new List<long>();
        var edges = new Dictionary<(int From, int To), (long Objects, int Folded)>();
        for (var p = 0; p < paths.Count; p++)
        {
            var path = paths[p];
            var sequence = new List<(int Node, int Folded)>();
            var folded = 0;
            sequence.Add((Node(DiagramNodeKind.Root, path.Root.Description, string.Create(CultureInfo.InvariantCulture, $"{path.Root.Kind}|{path.Root.Frozen}|{path.Root.Description}")), 0));
            foreach (var step in path.Steps)
            {
                if (!showFramework && step.IsFramework)
                {
                    folded++;
                    continue;
                }

                // The middle of a long path is one gap step; it belongs to its own path only (the 3.2 review).
                var gap = step.TypeName.StartsWith('(') && step.TypeName.EndsWith(" not shown)", StringComparison.Ordinal);
                sequence.Add((Node(DiagramNodeKind.Type, step.TypeName, gap ? string.Create(CultureInfo.InvariantCulture, $"gap {p}|{step.TypeName}") : step.TypeName), folded));
                folded = 0;
            }

            sequence.Add((Node(DiagramNodeKind.Target, target ?? path.TargetType, target is null ? path.TargetType : "set"), folded));
            var counted = new HashSet<int>();
            for (var i = 0; i < sequence.Count; i++)
            {
                var (node, foldedBefore) = sequence[i];
                layers[node] = Math.Min(layers[node], i);
                if (counted.Add(node))
                {
                    objects[node] = overlapping ? Math.Max(objects[node], path.Objects) : objects[node] + path.Objects;
                }

                if (i > 0 && sequence[i - 1].Node != node)
                {
                    var key = (sequence[i - 1].Node, node);
                    edges[key] = edges.TryGetValue(key, out var edge)
                        ? (overlapping ? Math.Max(edge.Objects, path.Objects) : edge.Objects + path.Objects, Math.Max(edge.Folded, foldedBefore))
                        : (path.Objects, foldedBefore);
                }
            }
        }

        // Targets sit below everything else.
        var bottom = labels.Select((l, i) => l.Kind == DiagramNodeKind.Target ? 0 : layers[i]).DefaultIfEmpty(0).Max() + 1;
        for (var i = 0; i < labels.Count; i++)
        {
            if (labels[i].Kind == DiagramNodeKind.Target)
            {
                layers[i] = bottom;
            }
        }

        var columns = new Dictionary<int, int>();
        var nodes = new List<DiagramNode>();
        for (var i = 0; i < labels.Count; i++)
        {
            var column = columns.TryGetValue(layers[i], out var used) ? used : 0;
            columns[layers[i]] = column + 1;
            nodes.Add(new DiagramNode(i, labels[i].Kind, labels[i].Label, objects[i], layers[i], column, column * (NodeWidth + ColumnGap), layers[i] * (NodeHeight + LayerGap)));
        }

        return new KeyPathDiagram(nodes, [.. edges.Select(e => new DiagramEdge(e.Key.From, e.Key.To, e.Value.Objects, e.Value.Folded, layers[e.Key.To] <= layers[e.Key.From]))]);

        int Node(DiagramNodeKind kind, string label, string identity)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"{(int)kind}|{identity}");
            if (!ids.TryGetValue(key, out var id))
            {
                id = labels.Count;
                ids[key] = id;
                labels.Add((kind, label));
                layers.Add(int.MaxValue);
                objects.Add(0);
            }

            return id;
        }
    }

    /// <summary>The node whose rectangle holds the point, or null.</summary>
    public DiagramNode? NodeAt(double x, double y) =>
        Nodes.FirstOrDefault(n => x >= n.X && x <= n.X + NodeWidth && y >= n.Y && y <= n.Y + NodeHeight);
}
