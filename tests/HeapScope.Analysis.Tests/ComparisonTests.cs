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

using HeapScope.Analysis.Sets;
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;

namespace HeapScope.Analysis.Tests;

/// <summary>Sprint 3.3 (ADR-032): objects matched by address, retained bytes and heap kinds in diffs, and trends.</summary>
public sealed class ComparisonTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Objects_match_by_address_and_type_and_a_different_type_at_an_address_is_dead_and_new()
    {
        // The synthetic builder places objects in creation order, so equal sizes give equal addresses on both sides.
        var before = new SyntheticGraphBuilder();
        var a1 = before.Object("App.A", 16);
        var b = before.Object("App.B", 16);
        var a2 = before.Object("App.A", 16);
        var after = new SyntheticGraphBuilder();
        var a1After = after.Object("App.A", 16);
        var c = after.Object("App.C", 16);
        var a2After = after.Object("App.A", 16);
        var a3 = after.Object("App.A", 16);
        using var baseline = before.Build();
        using var current = after.Build();
        (a1.Address, b.Address, a2.Address).ShouldBe((a1After.Address, c.Address, a2After.Address));

        var identity = ObjectIdentity.Match(baseline, current, Ct);

        identity.Survivors.ShouldBe(2);
        (identity.SurvivedInCurrent(a1After.Id), identity.SurvivedInCurrent(c.Id), identity.SurvivedInCurrent(a3.Id)).ShouldBe((true, false, false));
        identity.SurvivedInBaseline(b.Id).ShouldBeFalse("a C at a B's address is not the B");
        var newPerType = identity.NewPerType(current, Ct);
        var deadPerType = identity.DeadPerType(baseline, Ct);
        newPerType[current.TypeIndices[a3.Id]].ShouldBe(1);
        newPerType[current.TypeIndices[c.Id]].ShouldBe(1);
        deadPerType[baseline.TypeIndices[b.Id]].ShouldBe(1);
        deadPerType[baseline.TypeIndices[a1.Id]].ShouldBe(0);

        var allA = ObjectSet.OfIndices(current, [a1After.Id, a2After.Id, a3.Id]);
        identity.Select(allA, ObjectChangeKind.New).ShouldBe([a3.Id]);
        identity.Select(allA, ObjectChangeKind.Survived).ShouldBe([a1After.Id, a2After.Id]);
        identity.Select(ObjectSet.OfIndices(baseline, [b.Id]), ObjectChangeKind.Dead).ShouldBe([b.Id]);
    }

    [Fact]
    public void An_address_reused_by_another_size_a_younger_generation_another_heap_or_method_table_is_dead_and_new()
    {
        // The 3.3 review: an object never changes size, heap area or method table, and never gets younger.
        var before = new SyntheticGraphBuilder();
        before.Object("App.A", 32, HeapKind.Gen2, methodTable: 0x10);
        before.Object("App.A", 32, HeapKind.Gen1, methodTable: 0x10);
        before.Object("App.A", 32, HeapKind.Gen0, methodTable: 0x10);
        before.Object("App.A", 32, HeapKind.Gen2, methodTable: 0x10);
        before.Object("App.A", 32, HeapKind.Gen2, methodTable: 0x10);
        var after = new SyntheticGraphBuilder();
        after.Object("App.A", 32, HeapKind.Gen2, methodTable: 0x10);
        after.Object("App.A", 32, HeapKind.Gen0, methodTable: 0x10);
        after.Object("App.A", 32, HeapKind.Gen2, methodTable: 0x10);
        after.Object("App.A", 32, HeapKind.Gen2, methodTable: 0x20);
        after.Object("App.A", 24, HeapKind.Gen2, methodTable: 0x10);
        using var baseline = before.Build();
        using var current = after.Build();

        var identity = ObjectIdentity.Match(baseline, current, Ct);

        // Only the first (same everything) and the third (promoted from Gen0 to Gen2) survive; the second got younger, the
        // fourth has another method table, the last another size (it is last, so the others keep their addresses).
        (identity.Survivors, identity.ReusedAddresses).ShouldBe((2L, 3L));
        Enumerable.Range(0, 5).Select(identity.SurvivedInCurrent).ShouldBe([true, false, true, false, false]);
    }

    [Fact]
    public void An_object_on_another_heap_at_the_same_address_is_not_the_same_object()
    {
        var before = new SyntheticGraphBuilder();
        before.Object("App.Buffer", 4096, HeapKind.LargeObjectHeap);
        before.Object("App.Buffer", 4096, HeapKind.PinnedObjectHeap);
        var after = new SyntheticGraphBuilder();
        after.Object("App.Buffer", 4096, HeapKind.Gen2);
        after.Object("App.Buffer", 4096, HeapKind.PinnedObjectHeap);
        using var baseline = before.Build();
        using var current = after.Build();

        var identity = ObjectIdentity.Match(baseline, current, Ct);

        (identity.Survivors, identity.ReusedAddresses).ShouldBe((1L, 1L), "an LOH object never becomes a small object heap one");
        identity.SurvivedInCurrent(1).ShouldBeTrue();
    }

    [Fact]
    public void Frozen_objects_say_whether_two_dumps_are_one_run_of_a_process()
    {
        var before = new SyntheticGraphBuilder();
        before.Object("System.String", 32, HeapKind.Frozen);
        before.Object("System.String", 32, HeapKind.Frozen);
        var after = new SyntheticGraphBuilder();
        after.Object("System.String", 32, HeapKind.Frozen);
        after.Object("System.String", 40, HeapKind.Frozen);
        using var baseline = before.Build();
        using var current = after.Build();

        var identity = ObjectIdentity.Match(baseline, current, Ct);

        (identity.BaselineFrozen, identity.FrozenSurvivors).ShouldBe((2L, 1L));
    }

    [Fact]
    public void A_diff_carries_new_and_dead_objects_retained_bytes_and_heap_kinds_per_row()
    {
        var before = new SyntheticGraphBuilder();
        before.Object("App.A", 16, HeapKind.Gen2);
        before.Object("App.B", 16, HeapKind.Gen0);
        var after = new SyntheticGraphBuilder();
        after.Object("App.A", 16, HeapKind.Gen2);
        after.Object("App.A", 16, HeapKind.Gen0);
        using var baseline = before.Build();
        using var current = after.Build();
        var identity = ObjectIdentity.Match(baseline, current, Ct);
        var baseAggregation = TypeAggregation.Compute(baseline, cancellationToken: Ct);
        var currentAggregation = TypeAggregation.Compute(current, cancellationToken: Ct);
        var baseRetained = new GroupRetained([.. Enumerable.Range(0, baseline.Types.Count).Select(t => 100UL)], [], ByType: true);
        var currentRetained = new GroupRetained([.. Enumerable.Range(0, current.Types.Count).Select(t => 250UL)], [], ByType: true);

        var rows = TypeDiff.Build(
            baseline,
            baseAggregation,
            current,
            currentAggregation,
            GroupKey.Type,
            extras: new DiffExtras
            {
                NewPerCurrentType = identity.NewPerType(current, Ct),
                DeadPerBaselineType = identity.DeadPerType(baseline, Ct),
                BaselineRetained = baseRetained,
                CurrentRetained = currentRetained,
                Kinds = true,
            }).ToDictionary(r => r.Name);

        (rows["App.A"].NewObjects, rows["App.A"].DeadObjects).ShouldBe((1L, 0L), "the second A sits where the B was");
        (rows["App.B"].NewObjects, rows["App.B"].DeadObjects).ShouldBe((0L, 1L));
        (rows["App.A"].BaselineRetained, rows["App.A"].CurrentRetained, rows["App.A"].RetainedDelta).ShouldBe((100UL, 250UL, 150L));
        rows["App.A"].KindDelta(HeapKind.Gen0).ShouldBe(16);
        rows["App.A"].KindDelta(HeapKind.Gen2).ShouldBe(0);
        rows["App.B"].KindDelta(HeapKind.Gen0).ShouldBe(-16);
    }

    [Fact]
    public void Namespace_rows_take_their_groups_retained_bytes_by_name()
    {
        var before = new SyntheticGraphBuilder();
        before.Object("App.A", 16);
        before.Object("Lib.B", 16);
        using var baseline = before.Build();
        TypeGroups.GroupOfType(baseline, GroupKey.Namespace, out var names);
        var retained = new GroupRetained([.. names.Select(n => n == "App" ? 40UL : 7UL)], names, ByType: false);
        var aggregation = TypeAggregation.Compute(baseline, cancellationToken: Ct);

        var rows = TypeDiff.Build(baseline, aggregation, baseline, aggregation, GroupKey.Namespace, extras: new DiffExtras { BaselineRetained = retained, CurrentRetained = retained })
            .ToDictionary(r => r.Name);

        (rows["App"].BaselineRetained, rows["Lib"].CurrentRetained, rows["App"].NewObjects).ShouldBe((40UL, 7UL, null));
    }

    [Fact]
    public void A_trend_lists_each_snapshot_and_counts_the_steps_a_row_grew_in()
    {
        var graphs = new List<HeapGraph>();
        foreach (var count in new[] { 1, 3, 2, 5 })
        {
            var builder = new SyntheticGraphBuilder();
            for (var i = 0; i < count; i++)
            {
                builder.Object("App.Leak", 10);
            }

            builder.Object("App.Steady", 8);
            graphs.Add(builder.Build());
        }

        try
        {
            var rows = TypeTrend.Build([.. graphs.Select(g => (g, TypeAggregation.Compute(g, cancellationToken: Ct), (TypeInterfaces?)null))], GroupKey.Type).ToDictionary(r => r.Name);

            rows["App.Leak"].Counts.ShouldBe([1L, 3L, 2L, 5L]);
            (rows["App.Leak"].CountDelta, rows["App.Leak"].BytesDelta, rows["App.Leak"].GrowthSteps).ShouldBe((4L, 40L, 2));
            (rows["App.Steady"].GrowthSteps, rows["App.Steady"].TypeName).ShouldBe((0, "App.Steady"));
            Should.Throw<ArgumentException>(() => TypeTrend.Build([(graphs[0], TypeAggregation.Compute(graphs[0], cancellationToken: Ct), null)], GroupKey.Type));
        }
        finally
        {
            graphs.ForEach(g => g.Dispose());
        }
    }
}
