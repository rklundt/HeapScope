using HeapScope.Analysis.Inspections;
using HeapScope.Contracts;
using HeapScope.Core;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// Whether an inspection can run on a snapshot, and why not (sprint 1.6, ADR-020): a missing capability is explained as
/// for any feature; a missing loader fact on a full dump means its index cache predates the fact.
/// </summary>
internal static class InspectionAvailability
{
    /// <summary>Why <paramref name="inspection"/> cannot run, or null when it can.</summary>
    public static string? Explain(IInspection inspection, SnapshotCapabilities capabilities, SnapshotInfo info)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(info);
        var missing = capabilities.Missing(inspection.Requires);
        if (missing != SnapshotCapabilities.None)
        {
            // Contracts mirrors Core's capability flags (ADR-016); the lowest missing one explains it.
            return CapabilityDescriptions.ExplainMissing((SnapshotFeatures)(uint)LowestFlag((int)missing), (SnapshotKindDto)(int)info.Kind);
        }

        var facts = inspection.RequiresFacts & ~info.Facts;
        if (facts == SnapshotFacts.None)
        {
            return null;
        }

        return info.Kind == SnapshotKind.FullDump
            ? $"{inspection.Title} needs data HeapScope records since version 1.6, and this snapshot's index cache is older. Reopen the dump with Rebuild."
            : $"{inspection.Title} needs data this kind of snapshot does not record.";
    }

    /// <summary>The lowest set bit of a flags value.</summary>
    public static int LowestFlag(int flags) => flags & -flags;
}
