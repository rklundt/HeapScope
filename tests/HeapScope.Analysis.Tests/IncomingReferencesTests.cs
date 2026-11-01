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

namespace HeapScope.Analysis.Tests;

/// <summary>Sprint 2.1: who references an object or a type, with known answers.</summary>
public sealed class IncomingReferencesTests
{
    [Fact]
    public void An_object_lists_each_referrer_once_with_its_reference_count()
    {
        var builder = new SyntheticGraphBuilder();
        var cache = builder.Object("App.Cache");
        var list = builder.Object("App.List");
        var item = builder.Object("App.Item");
        builder.Reference(cache, item).Reference(list, item).Reference(list, item).Reference(item, item);
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: TestContext.Current.CancellationToken);

        var referrers = IncomingReferences.ForObject(reverse, item.Id);

        referrers.ShouldBe([new Referrer(cache.Id, 1), new Referrer(list.Id, 2), new Referrer(item.Id, 1)]);
        IncomingReferences.ForObject(reverse, cache.Id).ShouldBeEmpty();
    }

    [Fact]
    public void A_type_groups_its_referrers_by_their_type()
    {
        var builder = new SyntheticGraphBuilder();
        var orders = Enumerable.Range(0, 3).Select(_ => builder.Object("App.Order", 40)).ToArray();
        var cache = builder.Object("App.Cache", 100);
        var array = builder.Array("App.Order[]", 3, 8, references: true);
        var handler = builder.Object("App.Handler", 32);
        var other = builder.Object("App.Other", 16);

        // The cache holds orders 0 and 1, the array all three (order 2 twice), the handler order 2, and one order holds
        // another. Other references only the cache: not a referrer of Order.
        builder.Reference(cache, orders[0]).Reference(cache, orders[1]);
        builder.Reference(array, orders[0]).Reference(array, orders[1]).Reference(array, orders[2]).Reference(array, orders[2]);
        builder.Reference(handler, orders[2]);
        builder.Reference(orders[0], orders[1]);
        builder.Reference(other, cache);
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: TestContext.Current.CancellationToken);
        var types = TypeGroups.MatchTypes(graph, "App.Order", null, out var matched);

        var rows = IncomingReferences.ForTypes(graph, reverse, types, out var targetObjects, TestContext.Current.CancellationToken)
            .ToDictionary(r => graph.Types.GetName(r.TypeIndex));

        matched.ShouldBe(1);
        targetObjects.ShouldBe(3);
        rows.Keys.ShouldBe(["App.Order", "App.Cache", "App.Order[]", "App.Handler"], ignoreOrder: true);
        rows["App.Cache"].ShouldBe(rows["App.Cache"] with { Objects = 1, References = 2, Targets = 2, Bytes = 100 });
        rows["App.Order[]"].ShouldBe(rows["App.Order[]"] with { Objects = 1, References = 4, Targets = 3, Bytes = graph.GetSize(array.Id) });
        rows["App.Handler"].ShouldBe(rows["App.Handler"] with { Objects = 1, References = 1, Targets = 1, Bytes = 32 });
        rows["App.Order"].ShouldBe(rows["App.Order"] with { Objects = 1, References = 1, Targets = 1, Bytes = 40 });
    }

    [Fact]
    public void Types_are_matched_by_name_and_module_across_method_tables()
    {
        var builder = new SyntheticGraphBuilder();
        var first = builder.Object("App.Item", module: @"C:\app\App.dll", methodTable: 0x100);
        var second = builder.Object("App.Item", module: @"D:\copy\App.dll", methodTable: 0x200);
        var elsewhere = builder.Object("App.Item", module: "Other.dll");
        var holder = builder.Object("App.Holder", 24);
        builder.Reference(holder, first).Reference(holder, second).Reference(holder, elsewhere);
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: TestContext.Current.CancellationToken);

        var types = TypeGroups.MatchTypes(graph, "App.Item", "App.dll", out var matched);
        var rows = IncomingReferences.ForTypes(graph, reverse, types, out var targetObjects, TestContext.Current.CancellationToken);

        matched.ShouldBe(2, "two load contexts, one module file name");
        TypeGroups.MatchTypes(graph, "App.Item", Path.Combine("C:", "app", "bin", "App.dll"), out var byPath);
        byPath.ShouldBe(2, "a module given as a path matches by its file name, as sets do (peer review S5)");
        targetObjects.ShouldBe(2);
        rows.ShouldHaveSingleItem().ShouldBe(new ReferrerTypeRow(graph.TypeIndices[holder.Id], 1, 2, 2, 24));
        IncomingReferences.ForTypes(graph, reverse, TypeGroups.MatchTypes(graph, "App.None", null, out _), out var none, TestContext.Current.CancellationToken).ShouldBeEmpty();
        none.ShouldBe(0);
    }

    [Fact]
    public void Referrers_of_one_type_loaded_twice_are_one_row()
    {
        var builder = new SyntheticGraphBuilder();
        var target = builder.Object("App.Target");
        var first = builder.Object("App.Holder", 24, module: @"C:\one\App.dll", methodTable: 0x100);
        var second = builder.Object("App.Holder", 24, module: @"C:\two\App.dll", methodTable: 0x200);
        builder.Reference(first, target).Reference(second, target);
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: TestContext.Current.CancellationToken);

        var rows = IncomingReferences.ForTypes(graph, reverse, TypeGroups.MatchTypes(graph, "App.Target", null, out _), out _, TestContext.Current.CancellationToken);

        rows.ShouldHaveSingleItem().ShouldBe(new ReferrerTypeRow(graph.TypeIndices[first.Id], 2, 2, 1, 48));
    }
}
