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

namespace HeapScope.Contracts;

// DTOs are records of primitives, strings, arrays and Contracts enums only (ARCHITECTURE.md 4.4, ADR-016).
// Retained sizes (sprint 2.3): dominators of a snapshot, computed once and kept in a sidecar.

/// <summary>How a snapshot's dominators were obtained (sprint 2.3).</summary>
/// <param name="FromSidecar">Opened from the sidecar next to the index cache rather than computed.</param>
/// <param name="Seconds">Time to open or compute them (including the reverse references index when it was built first).</param>
/// <param name="Bytes">The kept columns: 12 bytes per object.</param>
/// <param name="Saved">Saved in the sidecar, so the next open reuses them.</param>
/// <param name="ReachableObjects">Objects a root reaches; the others have no dominator and retain nothing.</param>
/// <param name="CompleteGraph">False for an incomplete snapshot (a sampled gcdump, a truncated dump): references are missing,
/// so retained sizes may be overstated (review M6).</param>
/// <param name="Note">What to say beside retained sizes, in plain language: an incomplete snapshot, or a gcdump's objects
/// whose root it did not record (they retain nothing here); null when there is nothing to say.</param>
public sealed record DominatorIndexInfo(
    bool FromSidecar,
    SnapshotMemoryMode Mode,
    double Seconds,
    long Bytes,
    bool Saved,
    long ReachableObjects,
    bool CompleteGraph = true,
    string? Note = null);

/// <summary>One object's retained size and immediate dominator (sprint 2.3).</summary>
public sealed record RetainedRequest(Guid SnapshotId, ulong Address);

/// <summary>
/// An object's retained size: its own size plus the sizes of the objects only it keeps alive. <see cref="DominatorAddress"/>
/// is its immediate dominator, the closest object every path from a root to it passes through; 0 when only the roots
/// dominate it (<see cref="DominatedByRoots"/>: it is a root's object, or several roots hold it) or when no root reaches it
/// (<see cref="Reachable"/> false, explained by <see cref="UnreachableNote"/> for the snapshot's kind).
/// </summary>
public sealed record RetainedResult(
    ulong Address,
    string TypeName,
    long Bytes,
    bool Reachable,
    long RetainedBytes,
    ulong DominatorAddress,
    string? DominatorType,
    bool DominatedByRoots,
    string UnreachableNote,
    DominatorIndexInfo Index,
    bool CompleteGraph);
