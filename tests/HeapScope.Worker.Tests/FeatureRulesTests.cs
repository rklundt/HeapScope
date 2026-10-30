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
using HeapScope.Core.Synthetic;
using HeapScope.Worker.Rpc;
using HeapScope.Worker.Snapshots;

namespace HeapScope.Worker.Tests;

/// <summary>
/// The snapshot descriptor's feature availability list and the refusals come from one rule per feature (peer review P7).
/// </summary>
public sealed class FeatureRulesTests : IDisposable
{
    private const SnapshotFacts AllFacts = SnapshotFacts.FreeSpace | SnapshotFacts.ReferenceArrayMarks | SnapshotFacts.FinalizerRegistrations;

    private readonly SnapshotKinds _kinds = new(new WorkerOptions("pipe", null));
    private readonly List<ISnapshot> _opened = [];

    [Fact]
    public void A_complete_full_dump_offers_every_feature()
    {
        var snapshot = FullDump(SnapshotCapabilities.FullDump);

        var list = FeatureRules.List(snapshot);

        list.Select(f => f.Feature).ShouldBe(SnapshotFeatureIds.All);
        list.ShouldAllBe(f => f.Unavailable == null);
    }

    [Fact]
    public void A_full_dump_without_a_complete_graph_offers_no_unreachable_set_and_everything_else()
    {
        // As the loader marks a dump whose heap could not be read completely.
        var snapshot = FullDump(SnapshotCapabilities.FullDump & ~(SnapshotCapabilities.CompleteGraph | SnapshotCapabilities.ExactCounts));

        var list = FeatureRules.List(snapshot).ToDictionary(f => f.Feature, f => f.Unavailable);

        list[SnapshotFeatureIds.UnreachableSet].ShouldNotBeNull().ShouldContain("object graph is incomplete");
        list.Where(f => f.Key != SnapshotFeatureIds.UnreachableSet).ShouldAllBe(f => f.Value == null);
    }

    [Fact]
    public void A_gcdump_offers_none_of_them_and_says_why_for_each()
    {
        var snapshot = GcDump();

        var list = FeatureRules.List(snapshot).ToDictionary(f => f.Feature, f => f.Unavailable);

        list.Keys.ShouldBe(SnapshotFeatureIds.All, ignoreOrder: true);
        list[SnapshotFeatureIds.UnreachableSet].ShouldBe(SnapshotKinds.Describe(SnapshotKind.GcDump).WhyNoGarbage);
        list[SnapshotFeatureIds.InterfaceGrouping].ShouldNotBeNull().ShouldContain("this snapshot is a gcdump, which records no interfaces");
        list[SnapshotFeatureIds.FieldValues].ShouldBe(CapabilityDescriptions.ExplainMissing(SnapshotFeatures.FieldValues));
        list[SnapshotFeatureIds.StringValues].ShouldBe(CapabilityDescriptions.ExplainMissing(SnapshotFeatures.StringContents));
        list[SnapshotFeatureIds.Generations].ShouldBe(CapabilityDescriptions.ExplainMissing(SnapshotFeatures.Generations));
        list[SnapshotFeatureIds.FinalizableScope].ShouldBe("Listing finalizable objects needs data a gcdump does not record. Capture a full dump for it.");
    }

    [Fact]
    public void A_full_dump_index_cache_without_finalizer_registrations_points_at_rebuild()
    {
        var snapshot = FullDump(SnapshotCapabilities.FullDump, SnapshotFacts.None);

        FeatureRules.WhyUnavailable(SnapshotFeatureIds.FinalizableScope, snapshot).ShouldNotBeNull().ShouldContain("Rebuild");
    }

    [Theory]
    [InlineData(SnapshotFeatureIds.UnreachableSet, SnapshotCapabilities.None)]
    [InlineData(SnapshotFeatureIds.InterfaceGrouping, SnapshotCapabilities.FieldValues)]
    [InlineData(SnapshotFeatureIds.FieldValues, SnapshotCapabilities.FieldValues)]
    [InlineData(SnapshotFeatureIds.StringValues, SnapshotCapabilities.StringContents)]
    [InlineData(SnapshotFeatureIds.Generations, SnapshotCapabilities.Generations)]
    [InlineData(SnapshotFeatureIds.FinalizableScope, SnapshotCapabilities.None)]
    public void A_refusal_carries_the_listed_reason_and_the_capability_it_reads(string feature, SnapshotCapabilities missing)
    {
        var snapshot = GcDump();
        var listed = FeatureRules.List(snapshot).Single(f => f.Feature == feature).Unavailable;

        var refusal = Should.Throw<CapabilityNotSupportedException>(() => FeatureRules.Require(feature, snapshot));

        refusal.Message.ShouldBe(listed);
        refusal.Missing.ShouldBe(missing);
    }

    [Fact]
    public void A_missing_capability_a_row_source_reports_is_worded_by_its_feature_s_rule()
    {
        var gcdump = GcDump();

        FeatureRules.ExplainMissing(SnapshotCapabilities.StringContents, gcdump).ShouldBe(FeatureRules.WhyUnavailable(SnapshotFeatureIds.StringValues, gcdump));
        FeatureRules.ExplainMissing(SnapshotCapabilities.RootDetails, gcdump).ShouldBe(CapabilityDescriptions.ExplainMissing(SnapshotFeatures.RootDetails));
    }

    [Fact]
    public void Each_kind_says_whether_it_holds_garbage_and_what_an_unreached_object_means()
    {
        var full = SnapshotKinds.Describe(SnapshotKind.FullDump);
        var gcdump = SnapshotKinds.Describe(SnapshotKind.GcDump);

        (full.HoldsGarbage, gcdump.HoldsGarbage).ShouldBe((true, false));
        full.UnreachableNote.ShouldContain("garbage not yet collected");
        gcdump.UnreachableNote.ShouldContain("No recorded root");
        full.WhyNoObjectIdentity.ShouldBeNull();
        gcdump.WhyNoObjectIdentity.ShouldNotBeNull().ShouldStartWith("New and dead objects need two full dumps");
    }

    [Fact]
    public void The_descriptor_reads_the_list_and_counts_an_unlisted_feature_as_unavailable()
    {
        var descriptor = new SnapshotDescriptor(
            Guid.NewGuid(), "a.dmp", SnapshotKindDto.FullDump, SnapshotFeatures.ObjectGraph, null, null, null, null, 1, 0, 1, 0, 8, SnapshotMemoryMode.InMemory, false, null, 0, [],
            Availability: [new FeatureAvailability(SnapshotFeatureIds.Generations, null), new FeatureAvailability(SnapshotFeatureIds.UnreachableSet, "No.")]);

        descriptor.IsAvailable(SnapshotFeatureIds.Generations).ShouldBeTrue();
        descriptor.WhyUnavailable(SnapshotFeatureIds.UnreachableSet).ShouldBe("No.");
        descriptor.IsAvailable(SnapshotFeatureIds.FieldValues).ShouldBeFalse();
        descriptor.WhyUnavailable(SnapshotFeatureIds.FieldValues).ShouldNotBeNull().ShouldContain("did not list");
        (descriptor with { Availability = null }).IsAvailable(SnapshotFeatureIds.Generations).ShouldBeFalse();
    }

    public void Dispose() => _opened.ForEach(s => s.Dispose());

    // A full dump over a synthetic graph: its reader opens the dump only when something is read, which nothing here does.
    private ISnapshot FullDump(SnapshotCapabilities capabilities, SnapshotFacts facts = AllFacts) =>
        Keep(_kinds.For(SnapshotKind.FullDump).Open(new SnapshotInfo(SnapshotKind.FullDump, "a.dmp", Facts: facts), capabilities, SyntheticGraphs.Chain(3), "a.dmp", () => { }));

    private ISnapshot GcDump() =>
        Keep(_kinds.For(SnapshotKind.GcDump).Open(
            new SnapshotInfo(SnapshotKind.GcDump, "a.gcdump"),
            SnapshotCapabilities.ObjectGraph | SnapshotCapabilities.CompleteGraph | SnapshotCapabilities.ExactCounts | SnapshotCapabilities.ObjectAddresses,
            SyntheticGraphs.Chain(3),
            "a.gcdump",
            () => { }));

    private ISnapshot Keep(ISnapshot snapshot)
    {
        _opened.Add(snapshot);
        return snapshot;
    }
}
