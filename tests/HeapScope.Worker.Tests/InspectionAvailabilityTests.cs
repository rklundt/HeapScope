using HeapScope.Analysis.Inspections;
using HeapScope.Core;
using HeapScope.Worker.Rpc;

namespace HeapScope.Worker.Tests;

/// <summary>Which inspections run on which snapshots, and the reasons given otherwise (sprint 1.6, ADR-020).</summary>
public sealed class InspectionAvailabilityTests
{
    private static readonly SnapshotInfo s_fullDump = new(SnapshotKind.FullDump, "app.dmp", Facts: SnapshotFacts.FreeSpace | SnapshotFacts.ReferenceArrayMarks);

    [Fact]
    public void Every_inspection_runs_on_a_full_dump_loaded_by_this_version()
    {
        InspectionCatalog.All.ShouldAllBe(i => InspectionAvailability.Explain(i, SnapshotCapabilities.FullDump, s_fullDump) == null);
    }

    [Fact]
    public void Without_string_contents_duplicate_strings_is_unavailable_with_the_capability_reason()
    {
        // A gcdump has no field values or string text (1.8).
        var gcdump = SnapshotCapabilities.FullDump & ~(SnapshotCapabilities.FieldValues | SnapshotCapabilities.StringContents);
        var info = s_fullDump with { Kind = SnapshotKind.GcDump };

        InspectionAvailability.Explain(InspectionCatalog.Find("duplicate-strings")!, gcdump, info).ShouldBe("String contents are not stored in a gcdump. Capture a full dump to see string text.");
        InspectionAvailability.Explain(InspectionCatalog.Find("empty-collections")!, gcdump, info).ShouldBe("Field values are not stored in a gcdump. Capture a full dump to inspect fields.");
        InspectionAvailability.Explain(InspectionCatalog.Find("loh")!, gcdump, info).ShouldBeNull();
    }

    [Fact]
    public void A_missing_loader_fact_points_an_old_cache_at_rebuild()
    {
        var oldCache = s_fullDump with { Facts = SnapshotFacts.None, FreeBytes = null };

        InspectionAvailability.Explain(InspectionCatalog.Find("sparse-arrays")!, SnapshotCapabilities.FullDump, oldCache).ShouldNotBeNull().ShouldContain("Rebuild");
        InspectionAvailability.Explain(InspectionCatalog.Find("sparse-arrays")!, SnapshotCapabilities.FullDump, oldCache with { Kind = SnapshotKind.GcDump })
            .ShouldNotBeNull().ShouldContain("does not record");
        InspectionAvailability.Explain(InspectionCatalog.Find("fragmentation")!, SnapshotCapabilities.FullDump, oldCache).ShouldBeNull("it still shows pinning, with a note on free space");
    }

    [Theory]
    [InlineData(0b0110_0000, 0b0010_0000)]
    [InlineData(0b0100_0000, 0b0100_0000)]
    [InlineData(0, 0)]
    public void The_lowest_flag_is_isolated(int flags, int lowest)
    {
        InspectionAvailability.LowestFlag(flags).ShouldBe(lowest);
    }
}
