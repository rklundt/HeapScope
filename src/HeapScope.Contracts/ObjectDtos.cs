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
public sealed record InstancesRequest(
    Guid SnapshotId,
    string TypeName,
    string? Module,
    string? Filter = null,
    string? SortColumn = null,
    bool Descending = true,
    InstanceScope Scope = InstanceScope.All,
    ulong SameValueAs = 0);

/// <summary>Which objects of a type an instances query lists (sprint 1.6; inspection rows open these).</summary>
public enum InstanceScope
{
    All = 0,
    LargeObjectHeap = 1,
    Finalizable = 2,

    /// <summary>Strings whose text equals the string at <see cref="InstancesRequest.SameValueAs"/>.</summary>
    SameValue = 3,
}

/// <summary>
/// The answer to an instances query. Totals cover every object of the matched types (before the filter);
/// <see cref="ValuesAvailable"/> is false when the snapshot cannot read string text, so string values are empty and
/// value filters match nothing.
/// </summary>
public sealed record InstancesResult(ResultSetInfo Rows, long TotalObjects, long TotalBytes, int MatchedTypes, bool ValuesAvailable, double ElapsedSeconds);

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
