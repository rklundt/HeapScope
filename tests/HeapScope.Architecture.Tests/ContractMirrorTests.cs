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
    public void Snapshot_kind_dto_mirrors_snapshot_kind()
    {
        var core = Enum.GetValues<SnapshotKind>().ToDictionary(v => v.ToString(), v => (int)v);
        var wire = Enum.GetValues<SnapshotKindDto>().ToDictionary(v => v.ToString(), v => (int)v);

        wire.ShouldBe(core);
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
}
