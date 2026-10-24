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
            return ContractMapping.ExplainMissing(missing, info.Kind);
        }

        var facts = inspection.RequiresFacts & ~info.Facts;
        if (facts == SnapshotFacts.None)
        {
            return null;
        }

        return Explain(inspection.Title, info);
    }

    /// <summary>Why a feature named <paramref name="title"/> lacks a loader fact on this snapshot.</summary>
    public static string Explain(string title, SnapshotInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return Snapshots.SnapshotKinds.Describe(info.Kind).ExplainMissingFact(title);
    }
}
