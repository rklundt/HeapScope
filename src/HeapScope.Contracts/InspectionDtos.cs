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
// The simple inspections.

/// <summary>An inspection the worker offers for a snapshot (sprint 1.6).</summary>
/// <param name="Name">The CLI name (<c>duplicate-strings</c>).</param>
/// <param name="Unavailable">Why it cannot run on this snapshot (a missing capability), or null.</param>
public sealed record InspectionInfo(string Name, string Title, string Cause, string Fix, string? Unavailable);

/// <summary>Runs one inspection on a snapshot (sprint 1.6).</summary>
public sealed record InspectionRequest(Guid SnapshotId, string Name);

/// <summary>
/// An inspection's answer: findings, the bytes they cover, the bytes they waste (null when waste does not apply or is
/// unknown), a note on what could not be measured, and the rows (columns in <see cref="InspectionColumns"/>).
/// </summary>
public sealed record InspectionResultDto(string Name, long Count, long Bytes, long? WastedBytes, string? Note, ResultSetInfo Rows, double ElapsedSeconds);

/// <summary>
/// Column names of inspection rows. <see cref="TypeName"/>, <see cref="Module"/> and <see cref="Scope"/> are hidden: a
/// row with them opens the objects of that type in that <see cref="InstanceScope"/> (with <see cref="Address"/> for
/// <see cref="InstanceScope.SameValue"/>); a row with only an <see cref="Address"/> opens that object.
/// </summary>
public static class InspectionColumns
{
    public const string Address = "Address";
    public const string TypeName = "TypeName";
    public const string Module = "Module";
    public const string Scope = "Scope";

    /// <summary>Columns of the <see cref="InspectionNames.Fragmentation"/> rows, which the UI draws as bars.</summary>
    public const string Heap = "Heap";
    public const string Live = "Live";
    public const string Free = "Free";
    public const string PinnedObjects = "PinnedObjects";
    public const string PinnedBytes = "PinnedBytes";

    /// <summary>Whether a column says what a row opens rather than being shown.</summary>
    public static bool IsHidden(string column) => column is TypeName or Module or Scope;
}

/// <summary>Names of inspections that front ends present in their own way (sprint 1.6).</summary>
public static class InspectionNames
{
    /// <summary>Per heap kind rows (live, free, pinned) shown as bars as well as a grid.</summary>
    public const string Fragmentation = "fragmentation";
}
