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

using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Inspections;

/// <summary>
/// A one-click finding about common memory waste (sprint 1.6, ADR-020): a name for the CLI, a title, the cause and the
/// fix in one line each, the snapshot capabilities it needs, and a run over the graph and, when it needs dump values,
/// the typed reader. Inspections are stateless; <see cref="InspectionCatalog"/> lists them.
/// </summary>
public interface IInspection
{
    /// <summary>Lower-case name with dashes, as the CLI takes it (<c>duplicate-strings</c>).</summary>
    string Name { get; }

    string Title { get; }

    /// <summary>Why this costs memory, in one line.</summary>
    string Cause { get; }

    /// <summary>What usually fixes it, in one line.</summary>
    string Fix { get; }

    /// <summary>Capabilities the snapshot must have; without them the inspection is unavailable, never silently empty.</summary>
    SnapshotCapabilities Requires { get; }

    /// <summary>Facts the loader must have recorded (sprint 1.6 review); none for most inspections.</summary>
    SnapshotFacts RequiresFacts => SnapshotFacts.None;

    /// <summary>
    /// The rule's version: raised whenever what the inspection counts or its thresholds change, so a result disputed
    /// after an upgrade is traced to the rule that produced it (sprint 2.3.7 review M5). Logged with every result.
    /// </summary>
    int Version => 1;

    /// <summary>The thresholds the rule uses, as <c>Name=value</c> pairs for the log; empty when it has none.</summary>
    string Parameters => string.Empty;

    /// <summary>Memory waste (sprint 1.6) or a leak detector (sprint 2.6), which the front ends show in their own group.</summary>
    InspectionKind Kind => InspectionKind.Waste;

    /// <summary>Whether a baseline snapshot changes the answer (growth since it); the front ends offer one.</summary>
    bool UsesBaseline => false;

    /// <summary>Whether it needs the incoming references index (<see cref="InspectionContext.Referrers"/>).</summary>
    bool UsesReferrers => false;

    InspectionResult Run(InspectionContext context);
}

/// <summary>What an inspection finds (sprint 2.6).</summary>
public enum InspectionKind
{
    /// <summary>Memory spent for little (duplicate strings, empty collections...).</summary>
    Waste = 0,

    /// <summary>A leak pattern named with its cause (event handlers, timers, growing static collections...).</summary>
    Leak = 1,
}

/// <summary>An earlier snapshot of the same process, for growth (sprint 2.6); objects are matched by stable keys, never addresses (ADR-018).</summary>
public sealed record InspectionBaseline(HeapGraph Graph, SnapshotInfo Info, IObjectReader? Reader);

/// <summary>
/// What an inspection runs over. <see cref="Reader"/> is set when the snapshot has a typed reader; <see cref="Baseline"/>
/// when the user chose an earlier snapshot and the inspection uses one; <see cref="Referrers"/> when it asked for them
/// (sprint 2.6, ADR-020's derived data).
/// </summary>
public sealed record InspectionContext(
    HeapGraph Graph,
    SnapshotInfo Info,
    IObjectReader? Reader,
    IProgress<InspectionProgress>? Progress,
    CancellationToken CancellationToken,
    InspectionBaseline? Baseline = null,
    ReverseReferences? Referrers = null)
{
    public void Report(string phase, long done, long total) => Progress?.Report(new InspectionProgress(phase, done, total));
}

public sealed record InspectionProgress(string Phase, long Done, long Total);

/// <summary>
/// An inspection's answer: how many findings, the bytes they cover, the bytes they waste (null when "waste" does not
/// apply, as for the large object heap's contents), a note on what could not be measured, and the rows.
/// <paramref name="Basis"/> says what the answer rests on (objects scanned, skipped, groups compared), for the log
/// only: it backs up a disputed result without any value from the dump (sprint 2.3.7 review H3).
/// </summary>
public sealed record InspectionResult(long Count, long Bytes, long? WastedBytes, string? Note, InspectionTable Rows, string? Basis = null);

/// <summary>Kinds of inspection columns (mirrored by the worker's result columns).</summary>
public enum InspectionColumnKind
{
    Text = 0,
    Integer = 1,
    Bytes = 2,
    Address = 3,
    Percent = 4,
}

/// <param name="IsKey">The column that identifies a row (an object's address, or a type's name).</param>
public sealed record InspectionColumn(string Name, InspectionColumnKind Kind, bool IsKey = false);

/// <summary>
/// Column names shared by the inspections. <see cref="TypeName"/>, <see cref="Module"/> and <see cref="Scope"/> are
/// hidden: they say what a row opens (the objects of a type within a scope, or the strings equal to the one at
/// <see cref="Address"/>); a row with only an <see cref="Address"/> opens that object.
/// </summary>
public static class InspectionColumns
{
    public const string Type = "Type";
    public const string Objects = "Objects";
    public const string Ready = "Ready";
    public const string Bytes = "Bytes";
    public const string Address = "Address";
    public const string Length = "Length";
    public const string NullSlots = "NullSlots";
    public const string Wasted = "Wasted";
    public const string Generation = "Generation";
    public const string Count = "Count";
    public const string Capacity = "Capacity";
    public const string Value = "Value";
    public const string Copies = "Copies";
    public const string Heap = "Heap";
    public const string Live = "Live";
    public const string Free = "Free";
    public const string FreePercent = "FreePercent";
    public const string PinnedObjects = "PinnedObjects";
    public const string PinnedBytes = "PinnedBytes";
    public const string TypeName = "TypeName";
    public const string Module = "Module";
    public const string Scope = "Scope";

    // The leak detectors' columns (sprint 2.6).
    public const string Field = "Field";
    public const string Baseline = "Baseline";
    public const string Growth = "Growth";
    public const string Event = "Event";
    public const string Publishers = "Publishers";
    public const string Subscribers = "Subscribers";
    public const string SubscriberTypes = "SubscriberTypes";
    public const string Target = "Target";
    public const string Timers = "Timers";
}

/// <summary>Values of the hidden <see cref="InspectionColumns.Scope"/> column (names of the worker's instance scopes).</summary>
public static class InspectionScopes
{
    public const string LargeObjectHeap = "LargeObjectHeap";
    public const string Finalizable = "Finalizable";
    public const string SameValue = "SameValue";
}

/// <summary>An inspection's rows, formatted per page like any result set (invariant culture).</summary>
public abstract class InspectionTable
{
    public abstract IReadOnlyList<InspectionColumn> Columns { get; }

    public abstract int Count { get; }

    /// <summary>Approximate memory held by the rows.</summary>
    public abstract long EstimatedBytes { get; }

    public abstract void FormatRow(int index, string?[] cells);
}

/// <summary>Rows kept as compact records and formatted on demand.</summary>
public sealed class RowTable<TRow>(IReadOnlyList<InspectionColumn> columns, TRow[] rows, int bytesPerRow, Action<TRow, string?[]> format) : InspectionTable
{
    public override IReadOnlyList<InspectionColumn> Columns => columns;

    public override int Count => rows.Length;

    public override long EstimatedBytes => (long)rows.Length * bytesPerRow;

    public override void FormatRow(int index, string?[] cells) => format(rows[index], cells);
}

/// <summary>Every inspection, in the order the overview shows them.</summary>
public static class InspectionCatalog
{
    public static IReadOnlyList<IInspection> All { get; } =
    [
        new DuplicateStringsInspection(),
        new EmptyCollectionsInspection(),
        new OversizedCollectionsInspection(),
        new SparseArraysInspection(),
        new LargeArraysInspection(),
        new LargeObjectHeapInspection(),
        new FinalizableObjectsInspection(),
        new FragmentationInspection(),
        new GrowingStaticCollectionsDetector(),
        new EventHandlerLeaksDetector(),
        new TimerLeaksDetector(),
        new DisposedButReferencedDetector(),
        new HttpClientInstancesDetector(),
        new UndisposedServiceScopesDetector(),
    ];

    public static IInspection? Find(string name) => All.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
}
