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
using HeapScope.Worker.Snapshots;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// One rule per optional feature (<see cref="SnapshotFeatureIds"/>), from the snapshot's capabilities, its loader facts
/// and its kind handler. The same rule fills the descriptor's availability list when a snapshot opens and refuses the
/// feature's requests, so a front end offers only what the worker will do and shows the reason it would refuse with.
/// </summary>
internal static class FeatureRules
{
    /// <summary>Each feature this version knows, with null when <paramref name="snapshot"/> offers it or the reason.</summary>
    public static IReadOnlyList<FeatureAvailability> List(ISnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return [.. SnapshotFeatureIds.All.Select(id => new FeatureAvailability(id, WhyUnavailable(id, snapshot)))];
    }

    /// <summary>Why <paramref name="snapshot"/> does not offer <paramref name="feature"/>, or null when it does.</summary>
    public static string? WhyUnavailable(string feature, ISnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return feature switch
        {
            SnapshotFeatureIds.UnreachableSet => WhyNoUnreachableSet(snapshot),
            SnapshotFeatureIds.InterfaceGrouping => WhyNoInterfaces(snapshot),
            SnapshotFeatureIds.FieldValues => HasReader(snapshot, SnapshotCapabilities.FieldValues) ? null : Worded(SnapshotCapabilities.FieldValues, snapshot),
            SnapshotFeatureIds.StringValues => HasReader(snapshot, SnapshotCapabilities.StringContents) ? null : Worded(SnapshotCapabilities.StringContents, snapshot),
            SnapshotFeatureIds.Generations => snapshot.Capabilities.HasFlag(SnapshotCapabilities.Generations) ? null : Worded(SnapshotCapabilities.Generations, snapshot),
            SnapshotFeatureIds.FinalizableScope => snapshot.Info.Facts.HasFlag(SnapshotFacts.FinalizerRegistrations)
                ? null
                : InspectionAvailability.Explain("Listing finalizable objects", snapshot.Info),
            _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, "Unknown feature id."),
        };
    }

    /// <summary>
    /// Refuses <paramref name="feature"/> with its listed reason. The exception names the capability the feature reads, so
    /// callers that word a missing capability themselves keep doing so.
    /// </summary>
    /// <exception cref="CapabilityNotSupportedException">The snapshot does not offer the feature.</exception>
    public static void Require(string feature, ISnapshot snapshot)
    {
        if (WhyUnavailable(feature, snapshot) is { } why)
        {
            throw new CapabilityNotSupportedException(CapabilityOf(feature), why);
        }
    }

    /// <summary>
    /// Why interfaces cannot be read from <paramref name="snapshot"/>, naming it as <paramref name="side"/> (a trend names
    /// each of its snapshots), or null when they can.
    /// </summary>
    public static string? WhyNoInterfaces(ISnapshot snapshot, string side = "this snapshot")
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return HasReader(snapshot, SnapshotCapabilities.FieldValues)
            ? null
            : $"Interfaces need a full dump: {side} is a gcdump, which records no interfaces. Group by type, namespace or assembly, or capture a full dump.";
    }

    /// <summary>
    /// The reason for a capability a row source found missing: the rule of the feature that reads it, so the refusal
    /// matches the list; any other capability as Contracts words it for the snapshot's kind.
    /// </summary>
    public static string ExplainMissing(SnapshotCapabilities missing, ISnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var feature = (SnapshotCapabilities)(uint)ContractMapping.LowestFlag((int)missing) switch
        {
            SnapshotCapabilities.FieldValues => SnapshotFeatureIds.FieldValues,
            SnapshotCapabilities.StringContents => SnapshotFeatureIds.StringValues,
            SnapshotCapabilities.Generations => SnapshotFeatureIds.Generations,
            _ => null,
        };
        return (feature is null ? null : WhyUnavailable(feature, snapshot)) ?? Worded(missing, snapshot);
    }

    // A missing capability as Contracts words it for the snapshot's kind (a gcdump's "capture a full dump").
    private static string Worded(SnapshotCapabilities missing, ISnapshot snapshot) => ContractMapping.ExplainMissing(missing, snapshot.Info.Kind);

    private static string? WhyNoUnreachableSet(ISnapshot snapshot)
    {
        if (SnapshotKinds.Describe(snapshot.Info.Kind).WhyNoGarbage is { } noGarbage)
        {
            return noGarbage;
        }

        // References were lost reading this dump: live objects would show as garbage (section 5.2, as ADR-032).
        return SnapshotAccess.IsComplete(snapshot)
            ? null
            : "This dump's object graph is incomplete (some references could not be read), so live objects could look unreachable. Capture a new full dump.";
    }

    // Field values and string text are read from the original file, so the capability alone is not enough.
    private static bool HasReader(ISnapshot snapshot, SnapshotCapabilities capability) =>
        snapshot.Capabilities.HasFlag(capability) && snapshot.TryGetService<IObjectReader>(out var reader) && reader is not null;

    private static SnapshotCapabilities CapabilityOf(string feature) => feature switch
    {
        SnapshotFeatureIds.InterfaceGrouping or SnapshotFeatureIds.FieldValues => SnapshotCapabilities.FieldValues,
        SnapshotFeatureIds.StringValues => SnapshotCapabilities.StringContents,
        SnapshotFeatureIds.Generations => SnapshotCapabilities.Generations,
        _ => SnapshotCapabilities.None,
    };
}
