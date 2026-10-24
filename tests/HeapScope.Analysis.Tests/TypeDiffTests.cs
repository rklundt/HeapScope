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

using HeapScope.Analysis;
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;

namespace HeapScope.Analysis.Tests;

/// <summary>Per-type comparison of two snapshots (sprint 1.4, ADR-018).</summary>
public sealed class TypeDiffTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Rows_report_both_sides_their_deltas_and_new_and_gone_types()
    {
        var before = new SyntheticGraphBuilder();
        before.Object("App.Order", 40, module: "App.dll", methodTable: 0x10);
        before.Object("App.Order", 40, module: "App.dll", methodTable: 0x10);
        before.Object("App.Old", 100, module: "App.dll", methodTable: 0x20);
        var after = new SyntheticGraphBuilder();
        for (var i = 0; i < 5; i++)
        {
            after.Object("App.Order", 40, module: "App.dll", methodTable: 0x99);
        }

        after.Object("App.Fresh", 16, module: "App.dll", methodTable: 0x30);

        var rows = Diff(before, after, GroupKey.Type).ToDictionary(r => r.Name);

        rows["App.Order"].ShouldBe(new DiffRow("App.Order", 2, 80, 5, 200, TypeName: "App.Order", Module: "App.dll"));
        rows["App.Order"].CountDelta.ShouldBe(3);
        rows["App.Order"].BytesDelta.ShouldBe(120);
        rows["App.Old"].IsGone.ShouldBeTrue();
        rows["App.Old"].BytesDelta.ShouldBe(-100);
        rows["App.Fresh"].IsNew.ShouldBeTrue();
        rows.Count.ShouldBe(3);
    }

    [Fact]
    public void Types_sharing_a_key_in_one_snapshot_are_summed_and_other_modules_stay_apart()
    {
        // Two load contexts of Plugin.dll (two method tables) in the current snapshot; a same-named type in Fork.dll.
        var before = new SyntheticGraphBuilder();
        before.Object("Plugin.Cache", 40, module: @"C:\app\Plugin.dll", methodTable: 0x10);
        var after = new SyntheticGraphBuilder();
        after.Object("Plugin.Cache", 40, module: @"C:\app\Plugin.dll", methodTable: 0x10);
        after.Object("Plugin.Cache", 40, module: @"C:\app\Plugin.dll", methodTable: 0x20);
        after.Object("Plugin.Cache", 40, module: @"C:\fork\Fork.dll", methodTable: 0x30);

        var rows = Diff(before, after, GroupKey.Type).ToDictionary(r => r.Name);

        rows.Keys.Order(StringComparer.Ordinal).ShouldBe(["Plugin.Cache [Fork.dll]", "Plugin.Cache [Plugin.dll]"]);
        rows["Plugin.Cache [Plugin.dll]"].CountDelta.ShouldBe(1);
        rows["Plugin.Cache [Plugin.dll]"].CopiesChanged.ShouldBeTrue();
        rows["Plugin.Cache [Fork.dll]"].IsNew.ShouldBeTrue();
    }

    [Fact]
    public void A_stale_copy_of_a_reloaded_plugin_is_flagged_even_when_the_summed_counts_do_not_grow()
    {
        // Baseline: load context 1 holds 5 caches. Current: context 1 was not unloaded and still holds 1, the reloaded
        // context 2 holds 4. The sum is flat, so only the number of copies shows the leaked context.
        var before = new SyntheticGraphBuilder();
        for (var i = 0; i < 5; i++)
        {
            before.Object("Plugin.Cache", 40, module: "Plugin.dll", methodTable: 0x10);
        }

        var after = new SyntheticGraphBuilder();
        after.Object("Plugin.Cache", 40, module: "Plugin.dll", methodTable: 0x10);
        for (var i = 0; i < 4; i++)
        {
            after.Object("Plugin.Cache", 40, module: "Plugin.dll", methodTable: 0x20);
        }

        var row = Diff(before, after, GroupKey.Type).ShouldHaveSingleItem();

        row.CountDelta.ShouldBe(0);
        (row.BaselineTypes, row.CurrentTypes).ShouldBe((1, 2));
        row.CopiesChanged.ShouldBeTrue();
    }

    [Fact]
    public void Namespaces_and_assemblies_are_compared_as_groups()
    {
        var before = new SyntheticGraphBuilder();
        before.Object("App.Model.Order", 40, module: "App.dll", methodTable: 0x10);
        var after = new SyntheticGraphBuilder();
        after.Object("App.Model.Order", 40, module: "App.dll", methodTable: 0x10);
        after.Object("App.Model.Customer", 60, module: "App.dll", methodTable: 0x20);
        after.Object("System.String", 30, module: null, methodTable: 0x30);

        var namespaces = Diff(before, after, GroupKey.Namespace).ToDictionary(r => r.Name);
        namespaces["App.Model"].ShouldBe(new DiffRow("App.Model", 1, 40, 2, 100, BaselineTypes: 1, CurrentTypes: 2));
        namespaces["System"].IsNew.ShouldBeTrue();

        var assemblies = Diff(before, after, GroupKey.Assembly).ToDictionary(r => r.Name);
        assemblies["App.dll"].BytesDelta.ShouldBe(60);
        assemblies["(unknown assembly)"].CurrentCount.ShouldBe(1);
    }

    [Fact]
    public void An_empty_side_makes_every_row_new_or_gone()
    {
        var full = new SyntheticGraphBuilder();
        full.Object("App.Order", 40);

        Diff(new SyntheticGraphBuilder(), full, GroupKey.Type).ShouldHaveSingleItem().IsNew.ShouldBeTrue();
        Diff(full, new SyntheticGraphBuilder(), GroupKey.Type).ShouldHaveSingleItem().IsGone.ShouldBeTrue();
    }

    private static List<DiffRow> Diff(SyntheticGraphBuilder before, SyntheticGraphBuilder after, GroupKey groupBy)
    {
        using var baseline = before.Build();
        using var current = after.Build();
        return TypeDiff.Build(
            baseline,
            TypeAggregation.Compute(baseline, cancellationToken: Ct),
            current,
            TypeAggregation.Compute(current, cancellationToken: Ct),
            groupBy);
    }
}
