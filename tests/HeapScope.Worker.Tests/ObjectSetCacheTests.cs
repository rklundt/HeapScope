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

using HeapScope.Analysis.Inspections;
using HeapScope.Analysis.Sets;
using HeapScope.Contracts;
using HeapScope.Core.Synthetic;
using HeapScope.Worker.Rpc;

namespace HeapScope.Worker.Tests;

/// <summary>Sprint 3.1: resolved sets and inspection tables kept per snapshot, least recently used first out.</summary>
public sealed class ObjectSetCacheTests
{
    [Fact]
    public void Sets_are_found_by_snapshot_and_key_and_the_least_recently_used_go_first()
    {
        var cache = new ObjectSetCache();
        var snapshot = Guid.NewGuid();
        var set = Set();
        for (var i = 0; i < ObjectSetCache.MaxSets; i++)
        {
            cache.Add(snapshot, "k" + i, set);
        }

        cache.TryGet(snapshot, "k0").ShouldBeSameAs(set, "used now, so it stays");
        cache.Add(snapshot, "new", set);

        cache.TryGet(snapshot, "k0").ShouldNotBeNull();
        cache.TryGet(snapshot, "k1").ShouldBeNull("the least recently used went");
        cache.TryGet(Guid.NewGuid(), "k0").ShouldBeNull("another snapshot");
    }

    [Fact]
    public void Forgetting_a_snapshot_drops_its_sets_its_tables_and_the_tables_that_used_it_as_a_baseline()
    {
        var cache = new ObjectSetCache();
        var (snapshot, other, baseline) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var table = new RowTable<int>([], [], 0, (_, _) => { });
        cache.Add(snapshot, "k", Set());
        cache.Add(other, "k", Set());
        cache.AddTable(snapshot, "loh", null, table);
        cache.AddTable(other, "growing-static-collections", baseline, table);

        cache.Forget(snapshot);
        cache.Forget(baseline);

        cache.TryGet(snapshot, "k").ShouldBeNull();
        cache.TryGet(other, "k").ShouldNotBeNull();
        cache.TryGetTable(snapshot, "loh", null).ShouldBeNull();
        cache.TryGetTable(other, "growing-static-collections", baseline).ShouldBeNull();
    }

    [Fact]
    public void Equal_specs_have_equal_cache_keys_whatever_the_order_of_their_lists()
    {
        ObjectSetSpec.OfTypes([new("A", "a.dll"), new("B", null)]).CacheKey.ShouldBe(ObjectSetSpec.OfTypes([new("B", ""), new("A", "a.dll")]).CacheKey);
        ObjectSetSpec.OfObjects([3, 1, 3]).CacheKey.ShouldBe(ObjectSetSpec.OfObjects([1, 3]).CacheKey);
        ObjectSetSpec.OfNamespace("A").CacheKey.ShouldNotBe(ObjectSetSpec.OfAssembly("A").CacheKey);
        ObjectSetSpec.RetainedByOf(ObjectSetSpec.OfNamespace("A")).Shape.ShouldBe("retained by namespace");
        ObjectSetSpec.OfObjects([1, 2]).Shape.ShouldBe("objects (2 addresses)", "the log never sees the addresses");
    }

    private static ObjectSet Set()
    {
        var builder = new SyntheticGraphBuilder();
        var a = builder.Object("App.A", size: 10);
        return ObjectSet.OfIndices(builder.Build(), [a.Id]);
    }
}
