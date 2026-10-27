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
// Instances, single objects and references.

/// <summary>Column names of an instances result set (sprint 1.5).</summary>
public static class InstanceColumns
{
    public const string Address = "Address";
    public const string Type = "Type";
    public const string Bytes = "Bytes";
    public const string Generation = "Generation";

    /// <summary>A string's text (when the snapshot has string contents) or an array's length.</summary>
    public const string Value = "Value";

    /// <summary>The object's retained bytes (sprint 2.3, when asked for); empty for an object no root reaches.</summary>
    public const string Retained = "Retained";
}

/// <summary>
/// The objects of a type (sprint 1.5): every type whose name and module file name match, the key of the summary and
/// diff rows (ADR-018). The filter is an address prefix (<c>0x1f2a</c>), a regular expression on the value
/// (<c>/^Get.*Async$/</c>) or a case-insensitive substring of the value.
/// </summary>
/// <param name="Module">The module file name; null or empty matches types without one.</param>
/// <param name="SortColumn">A column of <see cref="InstanceColumns"/>; null sorts by bytes.</param>
/// <param name="Scope">Which of the type's objects (sprint 1.6): all, those on the large object heap, those still to be
/// finalized, or strings with the same text as the one at <paramref name="SameValueAs"/>.</param>
/// <param name="IncludeRetained">Adds the <see cref="InstanceColumns.Retained"/> column (sprint 2.3), computing the
/// snapshot's dominators on first use.</param>
public sealed record InstancesRequest(
    Guid SnapshotId,
    string TypeName,
    string? Module,
    string? Filter = null,
    string? SortColumn = null,
    bool Descending = true,
    InstanceScope Scope = InstanceScope.All,
    ulong SameValueAs = 0,
    bool IncludeRetained = false);

/// <summary>Which objects of a type an instances query lists (sprint 1.6; inspection rows open these).</summary>
public enum InstanceScope
{
    All = 0,
    LargeObjectHeap = 1,
    Finalizable = 2,

    /// <summary>Strings whose text equals the string at <see cref="InstancesRequest.SameValueAs"/>.</summary>
    SameValue = 3,

    /// <summary>
    /// Objects a root reaches (sprint 2.6 review H5): what a leak detector's row opens. Only part of the type, so the
    /// whole-type actions (Retained by set) stay off.
    /// </summary>
    Reachable = 4,
}

/// <summary>
/// The answer to an instances query. Totals cover every object of the matched types (before the filter);
/// <see cref="ValuesAvailable"/> is false when the snapshot cannot read string text, so string values are empty and
/// value filters match nothing.
/// </summary>
/// <param name="Dominators">How the dominators were obtained, when retained bytes were asked for.</param>
public sealed record InstancesResult(ResultSetInfo Rows, long TotalObjects, long TotalBytes, int MatchedTypes, bool ValuesAvailable, double ElapsedSeconds, DominatorIndexInfo? Dominators = null, string? FilterNote = null);

/// <summary>What an <see cref="ObjectFieldDto"/> holds (mirrors Core's <c>ObjectFieldKind</c>).</summary>
public enum ObjectFieldKindDto
{
    Primitive = 0,
    String = 1,
    Null = 2,
    Reference = 3,
    Struct = 4,
    Array = 5,
    More = 6,
}

/// <summary>A field or array element of an inspected object, formatted for display (sprint 1.5).</summary>
/// <param name="Address">The referenced object for references, strings and arrays; 0 otherwise.</param>
public sealed record ObjectFieldDto(string Name, string TypeName, ObjectFieldKindDto Kind, string Value, ulong Address, IReadOnlyList<ObjectFieldDto> Children);

/// <summary>Inspects one object (sprint 1.5).</summary>
public sealed record ObjectRequest(Guid SnapshotId, ulong Address);

/// <summary>
/// An object: what the graph knows (type, size, heap kind, outgoing reference count, whether a root holds it) and, when
/// the snapshot can read the dump, its fields (<see cref="Fields"/>) and string text (<see cref="Text"/>, the first 10,000
/// characters). <see cref="FieldsUnavailable"/> explains why fields are missing.
/// </summary>
public sealed record ObjectDetails(
    ulong Address,
    string TypeName,
    long Bytes,
    HeapKindDto HeapKind,
    int ReferenceCount,
    bool IsRooted,
    IReadOnlyList<ObjectFieldDto>? Fields,
    string? FieldsUnavailable,
    string? Text);

/// <summary>Column names of a references result set (sprint 1.5).</summary>
public static class ReferenceColumns
{
    /// <summary>The field path or array index holding the reference, when the snapshot has field values.</summary>
    public const string Field = "Field";
    public const string Address = "Address";
    public const string Type = "Type";
    public const string Bytes = "Bytes";
}

/// <summary>The objects one object references, in the graph's order (sprint 1.5).</summary>
public sealed record ReferencesRequest(Guid SnapshotId, ulong Address);

/// <summary>How the worker got a snapshot's reverse references index for a query (sprint 2.1).</summary>
/// <param name="FromSidecar">Opened from its sidecar file next to the index cache rather than built.</param>
/// <param name="Mode">Whether the index is held in memory or mapped from its sidecar.</param>
/// <param name="Seconds">Time spent getting the index for this query (0 when it was already loaded).</param>
/// <param name="Bytes">The index's size: 4 bytes per object and 4 per reference.</param>
/// <param name="Saved">The index is saved next to the index cache, so the next open of the snapshot reuses it.</param>
public sealed record ReverseIndexInfo(bool FromSidecar, SnapshotMemoryMode Mode, double Seconds, long Bytes, bool Saved);

/// <summary>Column names of a referrers result set (sprint 2.1): the objects referencing one object.</summary>
public static class ReferrerColumns
{
    /// <summary>The referrer's field path or array index holding the object, when the snapshot has field values.</summary>
    public const string Field = "Field";
    public const string Address = "Address";
    public const string Type = "Type";
    public const string Bytes = "Bytes";

    /// <summary>How many of the referrer's references point at the object (two fields holding it count 2).</summary>
    public const string References = "References";
}

/// <summary>The objects referencing one object, each once, in address order (sprint 2.1).</summary>
public sealed record ReferrersRequest(Guid SnapshotId, ulong Address);

/// <summary>
/// The answer to a referrers query. <see cref="ReferenceCount"/> counts references (a referrer holding the object twice
/// counts 2); <see cref="FieldsNote"/> says why the Field column is empty when it is. <see cref="CompleteGraph"/> is false
/// when the snapshot misses objects (a sampled gcdump, a truncated dump), so referrers may be missing (review M9).
/// </summary>
public sealed record ReferrersResult(ResultSetInfo Rows, long ReferenceCount, string? FieldsNote, ReverseIndexInfo Index, bool CompleteGraph);

/// <summary>Column names of a type referrers result set (sprint 2.1): the objects referencing a type, by their type.</summary>
public static class TypeReferrerColumns
{
    /// <summary>The referrers' type name.</summary>
    public const string Type = "Type";

    /// <summary>The referrers' module file name (with the name, the key of a type, ADR-018).</summary>
    public const string Module = "Module";

    /// <summary>Distinct referring objects of this type.</summary>
    public const string Objects = "Objects";

    /// <summary>References from them to the type's objects.</summary>
    public const string References = "References";

    /// <summary>Distinct objects of the type they reference.</summary>
    public const string Targets = "Targets";

    /// <summary>Shallow size of the referring objects.</summary>
    public const string Bytes = "Bytes";
}

/// <summary>
/// The objects referencing the objects of a type (name and module, like <see cref="InstancesRequest"/>), grouped by their
/// own type (sprint 2.1).
/// </summary>
/// <param name="SortColumn">A column of <see cref="TypeReferrerColumns"/>; null sorts by references.</param>
public sealed record TypeReferrersRequest(Guid SnapshotId, string TypeName, string? Module, string? SortColumn = null, bool Descending = true);

/// <summary>
/// The answer to a type referrers query: the matched types, their objects and the references into them.
/// <see cref="Index"/> is null when no type matched (the index is not built for nothing, review M8);
/// <see cref="CompleteGraph"/> as in <see cref="ReferrersResult"/>.
/// </summary>
public sealed record TypeReferrersResult(ResultSetInfo Rows, int MatchedTypes, long TargetObjects, long ReferenceCount, ReverseIndexInfo? Index, bool CompleteGraph);
