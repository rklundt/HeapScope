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

namespace HeapScope.Core;

/// <summary>Descriptive metadata of a snapshot. Never contains snapshot contents.</summary>
/// <param name="Kind">The kind of file the snapshot came from.</param>
/// <param name="SourcePath">Full path of the snapshot file, or a descriptive name for synthetic snapshots.</param>
/// <param name="ProcessName">Name of the captured process, when known.</param>
/// <param name="ProcessId">Id of the captured process, when known.</param>
/// <param name="RuntimeVersion">Version of the .NET runtime in the captured process, when known.</param>
/// <param name="PointerSize">Pointer size of the captured process in bytes.</param>
/// <param name="CapturedAtUtc">When the snapshot was captured, when known.</param>
/// <param name="FreeBytes">
/// Free space (the GC's free objects) per heap kind, indexed by <see cref="Graph.HeapKind"/>, for the fragmentation
/// summary (sprint 1.6); null when unknown (index caches written before 1.6, snapshots without segments).
/// </param>
/// <param name="Facts">What the loader recorded beyond the graph (sprint 1.6 review); none for older index caches.</param>
public sealed record SnapshotInfo(
    SnapshotKind Kind,
    string SourcePath,
    string? ProcessName = null,
    int? ProcessId = null,
    string? RuntimeVersion = null,
    int PointerSize = 8,
    DateTimeOffset? CapturedAtUtc = null,
    IReadOnlyList<long>? FreeBytes = null,
    SnapshotFacts Facts = SnapshotFacts.None);

/// <summary>
/// Facts a loader records beyond the graph (sprint 1.6): an inspection that needs one is unavailable without it (ADR-020),
/// for example on an index cache written before the loader recorded it.
/// </summary>
[Flags]
public enum SnapshotFacts
{
    None = 0,

    /// <summary>Free space per heap kind (<see cref="SnapshotInfo.FreeBytes"/>).</summary>
    FreeSpace = 1 << 0,

    /// <summary>Arrays of references are marked (<see cref="Graph.TypeKind.ReferenceArray"/>).</summary>
    ReferenceArrayMarks = 1 << 1,
}
