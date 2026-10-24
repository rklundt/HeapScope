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
// Type summaries and snapshot diffs.

/// <summary>What the rows of a type summary are grouped by.</summary>
public enum TypeGrouping
{
    Type = 0,
    Namespace = 1,
    Assembly = 2,
}

/// <summary>
/// Column names of a type summary result set (sorting takes any of them, sprint 1.5). In Type grouping the rows also carry
/// the hidden <see cref="TypeName"/> and <see cref="Module"/> columns: the key that opens a row's instances.
/// </summary>
public static class TypeSummaryColumns
{
    public const string Name = "Name";
    public const string Objects = "Objects";
    public const string Bytes = "Bytes";
    public const string Percent = "Percent";

    /// <summary>The raw type name (without the module suffix of colliding names).</summary>
    public const string TypeName = "TypeName";

    /// <summary>The module file name, empty when the type has none.</summary>
    public const string Module = "Module";
}

/// <summary>
/// Heap kinds, in the order the per-kind columns and totals use (mirrors Core's <c>HeapKind</c>). Unknown covers objects
/// whose generation the loader could not tell; it appears in the totals only when some bytes have it.
/// </summary>
public enum HeapKindDto
{
    Unknown = 0,
    Gen0 = 1,
    Gen1 = 2,
    Gen2 = 3,
    LargeObjectHeap = 4,
    PinnedObjectHeap = 5,
    Frozen = 6,
}

/// <summary>A type summary query (sprint 1.3).</summary>
/// <param name="SnapshotId">The snapshot.</param>
/// <param name="GroupBy">Rows per type, namespace or assembly.</param>
/// <param name="Filter">
/// Keeps rows whose name matches (<see cref="NameFilter"/>): an all lower-case filter ignores case; a filter with capitals
/// matches as typed or as CamelHumps; <c>*</c> matches any run of characters, ignoring case.
/// </param>
/// <param name="SortColumn">A result column name (<see cref="TypeSummaryColumns"/>); null sorts by bytes.</param>
/// <param name="Descending">Sort direction.</param>
public sealed record TypeSummaryRequest(
    Guid SnapshotId,
    TypeGrouping GroupBy = TypeGrouping.Type,
    string? Filter = null,
    string? SortColumn = null,
    bool Descending = true);

/// <summary>Totals for one heap kind.</summary>
public sealed record HeapKindTotal(HeapKindDto Kind, long Objects, long Bytes);

/// <summary>
/// The answer to a type summary: the rows as a result set (columns: name, objects, bytes, percent, then bytes per heap
/// kind when <see cref="HasGenerations"/>), and totals over the whole snapshot (not only the filtered rows). Totals and
/// rows count every object in the dump, including unreachable objects the GC has not collected yet; reachability comes
/// with the retention analysis (2.x).
/// </summary>
/// <param name="TotalTypes">Distinct types present in the snapshot.</param>
/// <param name="UnfilteredRows">Rows of the requested grouping before the filter (types, namespaces or assemblies).</param>
public sealed record TypeSummaryResult(
    ResultSetInfo Rows,
    long TotalObjects,
    long TotalBytes,
    int TotalTypes,
    bool HasGenerations,
    IReadOnlyList<HeapKindTotal> HeapKinds,
    double ElapsedSeconds,
    int UnfilteredRows);

/// <summary>A snapshot comparison query (sprint 1.4, ADR-018).</summary>
/// <param name="BaselineId">The earlier snapshot; deltas are current minus baseline.</param>
/// <param name="CurrentId">The later snapshot.</param>
/// <param name="GroupBy">Rows per type, namespace or assembly.</param>
/// <param name="Filter">Keeps rows whose name matches (<see cref="NameFilter"/>).</param>
/// <param name="SortColumn">The name of a result column to sort by; null sorts by <see cref="TypeDiffColumns.BytesDelta"/>.</param>
/// <param name="Descending">Sort direction.</param>
public sealed record TypeDiffRequest(
    Guid BaselineId,
    Guid CurrentId,
    TypeGrouping GroupBy = TypeGrouping.Type,
    string? Filter = null,
    string? SortColumn = null,
    bool Descending = true);

/// <summary>Column names of a type diff result set.</summary>
public static class TypeDiffColumns
{
    public const string Name = "Name";
    public const string Change = "Change";
    public const string BaselineTypes = "BaselineTypes";
    public const string CurrentTypes = "CurrentTypes";

    /// <summary>Hidden key column in Type grouping: the raw type name (sprint 1.5).</summary>
    public const string TypeName = "TypeName";

    /// <summary>Hidden key column in Type grouping: the module file name, empty when none (sprint 1.5).</summary>
    public const string Module = "Module";
    public const string BaselineObjects = "BaselineObjects";
    public const string CurrentObjects = "CurrentObjects";
    public const string ObjectsDelta = "ObjectsDelta";
    public const string BaselineBytes = "BaselineBytes";
    public const string CurrentBytes = "CurrentBytes";
    public const string BytesDelta = "BytesDelta";

    /// <summary>The <see cref="Change"/> value of a row present only in the current snapshot.</summary>
    public const string New = "new";

    /// <summary>The <see cref="Change"/> value of a row present only in the baseline.</summary>
    public const string Gone = "gone";

    /// <summary>
    /// The <see cref="Change"/> value of a row whose number of distinct types changed: the same type name and module
    /// loaded a different number of times (for example a plugin reloaded into a new load context while the old copy
    /// stays alive). The counts are in <see cref="BaselineTypes"/> and <see cref="CurrentTypes"/>.
    /// </summary>
    public const string Copies = "copies";
}

/// <summary>
/// The answer to a type diff: rows (columns in <see cref="TypeDiffColumns"/>) and totals. Snapshot totals cover every
/// object of each dump, unreachable ones included; the matching totals cover the rows that pass the filter (the whole
/// heap without one) and are what <c>diff --fail-on-growth</c> checks. <see cref="ExactCounts"/> is false when either
/// snapshot's counts are estimates (a sampled gcdump, ADR-003): the deltas are then estimates too.
/// </summary>
public sealed record TypeDiffResult(
    ResultSetInfo Rows,
    long BaselineObjects,
    long BaselineBytes,
    long CurrentObjects,
    long CurrentBytes,
    int NewRows,
    int GoneRows,
    int UnfilteredRows,
    long MatchingBaselineBytes,
    long MatchingBytesDelta,
    double ElapsedSeconds,
    bool ExactCounts);
