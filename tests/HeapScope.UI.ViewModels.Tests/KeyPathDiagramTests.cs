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

using HeapScope.Contracts;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>Sprint 3.2: key retention paths merged into one diagram.</summary>
public sealed class KeyPathDiagramTests
{
    [Fact]
    public void Paths_merge_into_one_node_per_root_and_type_with_their_objects_on_the_edges()
    {
        var diagram = KeyPathDiagram.Build(
            [
                Path("s_list", 30, ("App.List", false), ("App.Item[]", false)),
                Path("s_map", 20, ("App.Map", false), ("App.Item[]", false)),
                Path("s_list", 5, ("App.List", false), ("System.Collections.Generic.List", true), ("App.Item[]", false)),
            ],
            showFramework: false);

        diagram.Nodes.Select(n => (n.Kind, n.Label, n.Layer, n.Objects)).ShouldBe(
        [
            (DiagramNodeKind.Root, "static field s_list", 0, 35L),
            (DiagramNodeKind.Type, "App.List", 1, 35L),
            (DiagramNodeKind.Type, "App.Item[]", 2, 55L),
            (DiagramNodeKind.Target, "App.Item", 3, 55L),
            (DiagramNodeKind.Root, "static field s_map", 0, 20L),
            (DiagramNodeKind.Type, "App.Map", 1, 20L),
        ]);
        var edges = diagram.Edges.ToDictionary(e => (diagram.Nodes[e.From].Label, diagram.Nodes[e.To].Label));
        edges[("App.List", "App.Item[]")].Objects.ShouldBe(35);
        edges[("App.List", "App.Item[]")].Folded.ShouldBe(1, "the framework step is folded into the edge");
        edges[("App.Item[]", "App.Item")].Objects.ShouldBe(55);
        diagram.MaxEdgeObjects.ShouldBe(55);
        diagram.Edges.ShouldAllBe(e => !e.IsBack);
        (diagram.Nodes[4].Column, diagram.Nodes[5].Column).ShouldBe((1, 1), "the second path's own nodes sit beside the first's");
        diagram.NodeAt(diagram.Nodes[5].X + 1, diagram.Nodes[5].Y + 1).ShouldBe(diagram.Nodes[5]);
        diagram.NodeAt(-5, -5).ShouldBeNull();
    }

    [Fact]
    public void A_type_met_higher_on_another_path_is_one_node_and_the_edge_back_up_says_so()
    {
        var diagram = KeyPathDiagram.Build(
            [
                Path("s_a", 10, ("App.Node", false)),
                Path("s_b", 5, ("App.Holder", false), ("App.Cache", false), ("App.Node", false), ("App.Holder", false)),
            ],
            showFramework: true);

        diagram.Nodes.Count(n => n.Label == "App.Node").ShouldBe(1);
        diagram.Edges.Single(e => diagram.Nodes[e.From].Label == "App.Node" && diagram.Nodes[e.To].Label == "App.Holder").IsBack.ShouldBeTrue();
        diagram.Nodes.Single(n => n.Kind == DiagramNodeKind.Target).Layer.ShouldBe(diagram.Nodes.Where(n => n.Kind != DiagramNodeKind.Target).Max(n => n.Layer) + 1);
        KeyPathDiagram.Empty.Nodes.ShouldBeEmpty();
        (KeyPathDiagram.Empty.Width, KeyPathDiagram.Empty.Height).ShouldBe((0d, 0d));
    }

    private static KeyPathDto Path(string root, long objects, params (string Type, bool Framework)[] steps) =>
        new(new PathRootDto(RootKindDto.StaticField, false, "static field " + root), [.. steps.Select(s => new PathStepDto(s.Type, s.Framework))], "App.Item", objects, objects * 10, 0x1000);
}
