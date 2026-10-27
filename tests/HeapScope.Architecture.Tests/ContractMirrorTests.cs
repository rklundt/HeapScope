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
using HeapScope.Core;

namespace HeapScope.Architecture.Tests;

/// <summary>ADR-016: Contracts mirrors Core vocabulary on the wire; names and values must stay identical.</summary>
public class ContractMirrorTests
{
    [Fact]
    public void Snapshot_features_mirror_snapshot_capabilities()
    {
        var core = Enum.GetValues<SnapshotCapabilities>()
            .Where(v => v != SnapshotCapabilities.FullDump)
            .ToDictionary(v => v.ToString(), v => (uint)v);
        var wire = Enum.GetValues<SnapshotFeatures>().ToDictionary(v => v.ToString(), v => (uint)v);

        wire.ShouldBe(core);
    }

    [Fact]
    public void Heap_kind_dto_mirrors_heap_kind()
    {
        var core = Enum.GetValues<HeapScope.Core.Graph.HeapKind>().ToDictionary(v => v.ToString(), v => (int)v);
        var wire = Enum.GetValues<HeapKindDto>().ToDictionary(v => v.ToString(), v => (int)v);

        wire.ShouldBe(core);
    }

    [Fact]
    public void Root_kind_dto_mirrors_root_kind()
    {
        var core = Enum.GetValues<HeapScope.Core.Graph.RootKind>().ToDictionary(v => v.ToString(), v => (int)v);
        var wire = Enum.GetValues<RootKindDto>().ToDictionary(v => v.ToString(), v => (int)v);

        wire.ShouldBe(core);
    }

    [Fact]
    public void Group_key_mirrors_type_grouping()
    {
        var analysis = Enum.GetValues<HeapScope.Analysis.GroupKey>().ToDictionary(v => v.ToString(), v => (int)v);
        var wire = Enum.GetValues<TypeGrouping>().ToDictionary(v => v.ToString(), v => (int)v);

        wire.ShouldBe(analysis);
    }

    [Fact]
    public void Snapshot_kind_dto_mirrors_snapshot_kind()
    {
        var core = Enum.GetValues<SnapshotKind>().ToDictionary(v => v.ToString(), v => (int)v);
        var wire = Enum.GetValues<SnapshotKindDto>().ToDictionary(v => v.ToString(), v => (int)v);

        wire.ShouldBe(core);
    }

    [Fact]
    public void Inspection_vocabulary_mirrors_the_wire()
    {
        // Sprint 1.6 review: the scope cells name the contract's instance scopes, the shared column names match, and the
        // fragmentation inspection is the one front ends draw as bars.
        var scopes = typeof(HeapScope.Analysis.Inspections.InspectionScopes).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToList();
        scopes.ShouldAllBe(s => Enum.IsDefined(Enum.Parse<InstanceScope>(s)));
        scopes.ShouldBe(Enum.GetNames<InstanceScope>().Where(n => n != nameof(InstanceScope.All)), ignoreOrder: true);

        var wire = typeof(InspectionColumns).GetFields().ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);
        var analysis = typeof(HeapScope.Analysis.Inspections.InspectionColumns).GetFields().ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);
        wire.ShouldAllBe(c => analysis.ContainsKey(c.Key) && analysis[c.Key] == c.Value);
        HeapScope.Analysis.Inspections.InspectionCatalog.Find(InspectionNames.Fragmentation).ShouldNotBeNull();
    }

    [Fact]
    public void Every_single_feature_has_a_specific_explanation()
    {
        foreach (var feature in Enum.GetValues<SnapshotFeatures>().Where(f => f != SnapshotFeatures.None))
        {
            CapabilityDescriptions.ExplainMissing(feature).ShouldNotStartWith("This snapshot does not support");
            CapabilityDescriptions.ExplainMissing(feature, SnapshotKindDto.FullDump).ShouldNotStartWith("This snapshot does not support");
            CapabilityDescriptions.ExplainMissing(feature, SnapshotKindDto.FullDump).ShouldNotContain("Capture a full dump", Case.Insensitive, feature.ToString());
        }
    }

    [Fact]
    public void The_dominator_trees_wire_vocabulary_mirrors_the_analysis()
    {
        // Sprint 2.4 (the 2.4 review): Contracts owns the tree's kinds, groupings, keys and limits; Analysis mirrors them.
        Enum.GetValues<DominatorGroupingDto>().ToDictionary(v => v.ToString(), v => (int)v)
            .ShouldBe(Enum.GetValues<HeapScope.Analysis.Retention.DominatorGrouping>().ToDictionary(v => v.ToString(), v => (int)v));
        Enum.GetNames<HeapScope.Analysis.Retention.DominatorRowKind>().ShouldBe([DominatorRowKinds.Object, DominatorRowKinds.Group, DominatorRowKinds.More], ignoreOrder: true);

        const ulong address = 0x1f2a3b40;
        var listKey = HeapScope.Analysis.Retention.DominatorTreeRows.ObjectKey(address);
        listKey.ShouldBe(DominatorTreeKeys.Object(address));
        HeapScope.Analysis.Retention.DominatorTreeRows.RootKey.ShouldBe(DominatorTreeKeys.Root);
        HeapScope.Analysis.Retention.DominatorTreeRows.GroupKey(listKey, HeapScope.Analysis.Retention.DominatorTreeRows.GroupName("App.Order", "App.dll"))
            .ShouldBe(DominatorTreeKeys.TypeGroup(listKey, "App.Order", "App.dll"));
        HeapScope.Analysis.Retention.DominatorTreeRows.GroupKey(listKey, HeapScope.Analysis.Retention.DominatorTreeRows.GroupName("App.Order", null))
            .ShouldBe(DominatorTreeKeys.TypeGroup(listKey, "App.Order", null));
        HeapScope.Analysis.Retention.DominatorTreeRows.GroupKey(listKey, HeapScope.Analysis.Retention.DominatorTreeRows.GroupName("App.Models", null))
            .ShouldBe(DominatorTreeKeys.NamespaceGroup(listKey, "App.Models"));
        HeapScope.Analysis.Retention.DominatorTreeRows.MoreKey(listKey, 200).ShouldBe(DominatorTreeKeys.More(listKey, 200));

        (DominatorTreeLimits.MaxRows, DominatorTreeLimits.MaxDepth, DominatorTreeLimits.TrailHead, DominatorTreeLimits.TrailTail).ShouldBe((
            HeapScope.Analysis.Retention.DominatorTreeRows.MaxRows,
            HeapScope.Analysis.Retention.DominatorTreeRows.MaxDepth,
            HeapScope.Analysis.Retention.DominatorTreeRows.TrailHead,
            HeapScope.Analysis.Retention.DominatorTreeRows.TrailTail));
    }

    [Fact]
    public void The_allocation_trees_wire_vocabulary_mirrors_the_analysis()
    {
        // Sprint 2.5: Contracts owns the allocation trees' kinds, keys and limits; Analysis mirrors them.
        Enum.GetValues<AllocationTreeKindDto>().ToDictionary(v => v.ToString(), v => (int)v)
            .ShouldBe(Enum.GetValues<HeapScope.Analysis.Allocations.AllocationTreeKind>().ToDictionary(v => v.ToString(), v => (int)v));
        Enum.GetNames<HeapScope.Analysis.Allocations.AllocationRowKind>().ShouldBe([AllocationRowKinds.Frame, AllocationRowKinds.More], ignoreOrder: true);

        HeapScope.Analysis.Allocations.AllocationTreeRows.RootKey.ShouldBe(AllocationTreeKeys.Root);
        var nodeKey = HeapScope.Analysis.Allocations.AllocationTreeRows.NodeKey(12);
        nodeKey.ShouldBe(AllocationTreeKeys.Node(12));
        var backTrace = HeapScope.Analysis.Allocations.AllocationTreeRows.BackTraceKey(AllocationTreeKeys.Root, 7);
        backTrace.ShouldBe(AllocationTreeKeys.BackTrace(AllocationTreeKeys.Root, 7));
        HeapScope.Analysis.Allocations.AllocationTreeRows.BackTraceKey(backTrace, 3).ShouldBe(AllocationTreeKeys.BackTrace(backTrace, 3));
        HeapScope.Analysis.Allocations.AllocationTreeRows.MoreKey(nodeKey, 100).ShouldBe(AllocationTreeKeys.More(nodeKey, 100));
        HeapScope.Analysis.Allocations.AllocationTreeRows.MoreKey(AllocationTreeKeys.Root, 100).ShouldBe(AllocationTreeKeys.More(AllocationTreeKeys.Root, 100));

        (AllocationTreeLimits.MaxRows, AllocationTreeLimits.MaxDepth).ShouldBe((
            HeapScope.Analysis.Allocations.AllocationTreeRows.MaxRows,
            HeapScope.Analysis.Allocations.AllocationTreeRows.MaxDepth));
    }
}
