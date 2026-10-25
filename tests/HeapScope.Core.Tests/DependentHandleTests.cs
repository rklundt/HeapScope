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
using HeapScope.Core.Synthetic;

namespace HeapScope.Core.Tests;

/// <summary>Sprint 2.2: the graph remembers which edges came from dependent handles.</summary>
public sealed class DependentHandleTests
{
    [Fact]
    public void Dependent_handle_edges_are_recorded_sorted_once_and_told_apart_from_fields()
    {
        var builder = new SyntheticGraphBuilder();
        var key = builder.Object("App.Key");
        var value = builder.Object("App.Value");
        var other = builder.Object("App.Other");
        builder.DependentHandle(other, value).DependentHandle(key, value).DependentHandle(key, value).Reference(key, other);
        using var graph = builder.Build();

        graph.DependentHandles.Span.ToArray().ShouldBe([new DependentHandle(key.Id, value.Id), new DependentHandle(other.Id, value.Id)]);
        graph.IsDependentHandleEdge(key.Id, value.Id).ShouldBeTrue();
        graph.IsDependentHandleEdge(key.Id, other.Id).ShouldBeFalse("a field reference");
        graph.IsDependentHandleEdge(value.Id, key.Id).ShouldBeFalse();
        graph.GetReferences(key.Id).ToArray().ShouldContain(value.Id, "the edge is still in the CSR");
    }

    [Fact]
    public void A_graph_without_dependent_handles_has_none()
    {
        using var graph = SyntheticGraphs.Chain(3);

        graph.DependentHandles.Length.ShouldBe(0);
        graph.IsDependentHandleEdge(0, 1).ShouldBeFalse();
    }

    [Fact]
    public void Columns_naming_objects_outside_the_graph_are_refused()
    {
        using var graph = SyntheticGraphs.Chain(2);
        var columns = new HeapGraphColumns(
            graph.Addresses, graph.TypeIndices, graph.Sizes, graph.HeapKinds, graph.Flags, graph.EdgeStarts, graph.Edges, graph.LargeSizes);

        Should.Throw<ArgumentException>(() => HeapGraph.FromColumns(
            columns, graph.Types, graph.Roots, graph.Strings, 0, 0, null, Column<DependentHandle>.FromArray([new DependentHandle(0, 5)])));
    }
}
