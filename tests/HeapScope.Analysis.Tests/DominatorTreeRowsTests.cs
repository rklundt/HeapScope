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

using HeapScope.Analysis.Retention;
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;

namespace HeapScope.Analysis.Tests;

/// <summary>The flattened dominator tree (sprint 2.4, ADR-027): what the retainers grid and the CLI show.</summary>
public sealed class DominatorTreeRowsTests : IDisposable
{
    private static readonly HashSet<string> s_none = [];

    private readonly HeapGraph _graph;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly Dominators _dominators;
    private readonly SyntheticObject _cache;
    private readonly SyntheticObject _small;
    private readonly SyntheticObject _shared;

    // A static cache holds 5 entries of 100 bytes each (one with a 300-byte payload); a small list holds 2 nodes; an object
    // two roots share sits on the top level.
    public DominatorTreeRowsTests()
    {
        var builder = new SyntheticGraphBuilder();
        _cache = builder.Object("App.Cache", size: 40, module: "App.dll");
        _small = builder.Object("App.List", size: 30, module: "App.dll");
        _shared = builder.Object("Lib.Shared", size: 20, module: "Lib.dll");
        var other = builder.Object("App.Other", size: 10, module: "App.dll");
        for (var i = 0; i < 5; i++)
        {
            var entry = builder.Object("App.Entry", size: 100, module: "App.dll");
            builder.Reference(_cache, entry);
            if (i == 2)
            {
                var payload = builder.Object("System.Byte[]", size: 300);
                builder.Reference(entry, payload);
            }
        }

        for (var i = 0; i < 2; i++)
        {
            var node = builder.Object("App.Node", size: 8, module: "App.dll");
            builder.Reference(_small, node);
        }

        builder.Reference(_cache, _shared).Reference(other, _shared);
        builder.Root(_cache).Root(_small, RootKind.StrongHandle).Root(other, RootKind.StrongHandle);
        _graph = builder.Build();
        using var reverse = ReverseReferences.Build(_graph, cancellationToken: Ct);
        _dominators = Dominators.Compute(_graph, reverse, cancellationToken: Ct);
    }

    [Fact]
    public void The_top_level_is_the_biggest_retainers_in_retained_order()
    {
        var view = DominatorTreeRows.Build(_graph, _dominators, -1, s_none, DominatorGrouping.None, 100, Ct);

        view.Rows.Select(r => (r.Name, r.Retained, r.Depth)).ShouldBe([("App.Cache", 840UL, 0), ("App.List", 46UL, 0), ("Lib.Shared", 20UL, 0), ("App.Other", 10UL, 0)]);
        view.Rows[0].Key.ShouldBe(DominatorTreeRows.ObjectKey(_cache.Address));
        view.Rows[0].Children.ShouldBe(5, "the shared object is not the cache's: another root holds it too");
        view.TotalRetained.ShouldBe(916UL);
        view.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void An_expanded_row_shows_its_children_below_it_and_a_more_row_sums_the_rest()
    {
        var cacheKey = DominatorTreeRows.ObjectKey(_cache.Address);
        var view = DominatorTreeRows.Build(_graph, _dominators, -1, new HashSet<string> { cacheKey }, DominatorGrouping.None, maxChildren: 2, Ct);

        view.Rows.Select(r => (r.Kind, r.Name, r.Depth, r.Retained)).ShouldBe(
        [
            (DominatorRowKind.Object, "App.Cache", 0, 840UL),
            (DominatorRowKind.Object, "App.Entry", 1, 400UL),
            (DominatorRowKind.Object, "App.Entry", 1, 100UL),
            (DominatorRowKind.More, "3 more objects", 1, 300UL),
            (DominatorRowKind.Object, "App.List", 0, 46UL),
            (DominatorRowKind.More, "2 more objects", 0, 30UL),
        ]);
        view.Rows[1].ParentKey.ShouldBe(cacheKey);
        view.Rows[3].Objects.ShouldBe(3);

        // Expanding the "more" row shows the next batch, and the keys of the rows already shown stay the same.
        var more = view.Rows[3].Key;
        var next = DominatorTreeRows.Build(_graph, _dominators, -1, new HashSet<string> { cacheKey, more }, DominatorGrouping.None, maxChildren: 2, Ct);
        next.Rows.Count(r => r.ParentKey == cacheKey && r.Kind == DominatorRowKind.Object).ShouldBe(4);
        next.Rows.Take(3).Select(r => r.Key).ShouldBe(view.Rows.Take(3).Select(r => r.Key));
    }

    [Fact]
    public void Grouped_children_sum_their_members_exactly_and_expand_into_them()
    {
        // The top level's groups first: the cache's group, then the cache, then its entries.
        var cacheGroup = DominatorTreeRows.GroupKey(DominatorTreeRows.RootKey, "App.Cache [App.dll]");
        var cacheKey = DominatorTreeRows.ObjectKey(_cache.Address);
        var entriesKey = DominatorTreeRows.GroupKey(cacheKey, "App.Entry [App.dll]");
        var view = DominatorTreeRows.Build(_graph, _dominators, -1, new HashSet<string> { cacheGroup, cacheKey, entriesKey }, DominatorGrouping.Type, 100, Ct);

        var group = view.Rows.Single(r => r.Kind == DominatorRowKind.Group && r.ParentKey == cacheKey);
        (group.Name, group.Module, group.Objects, group.Retained, group.Bytes, group.Expanded).ShouldBe(("App.Entry", "App.dll", 5L, 800UL, 500UL, true));
        view.Rows.Count(r => r.ParentKey == entriesKey).ShouldBe(5);
        view.Rows.Where(r => r.ParentKey == DominatorTreeRows.RootKey).Select(r => r.Name).ShouldBe(["App.Cache", "App.List", "Lib.Shared", "App.Other"]);

        var byNamespace = DominatorTreeRows.Build(_graph, _dominators, -1, s_none, DominatorGrouping.Namespace, 100, Ct);
        byNamespace.Rows.Select(r => (r.Name, r.Objects, r.Retained)).ShouldBe([("App", 3L, 896UL), ("Lib", 1L, 20UL)]);
    }

    [Fact]
    public void A_focus_shows_itself_expanded_with_its_ancestors()
    {
        var entry = _dominators.ChildrenOf(_cache.Id)[0];
        var view = DominatorTreeRows.Build(_graph, _dominators, entry, s_none, DominatorGrouping.None, 100, Ct);

        view.Rows[0].Object.ShouldBe(entry);
        view.Rows[0].Expanded.ShouldBeTrue();
        view.Rows.Skip(1).ShouldAllBe(r => r.Depth == 1);
        view.Ancestors.ShouldBe([_cache.Id]);
        view.FocusRetained.ShouldBe(400UL);
    }

    [Fact]
    public void Arguments_are_checked()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => DominatorTreeRows.Build(_graph, _dominators, _graph.ObjectCount, s_none, DominatorGrouping.None, 100, Ct));
        Should.Throw<ArgumentOutOfRangeException>(() => DominatorTreeRows.Build(_graph, _dominators, -1, s_none, DominatorGrouping.None, 0, Ct));
    }

    public void Dispose()
    {
        _dominators.Dispose();
        _graph.Dispose();
    }
}
