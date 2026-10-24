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

    InspectionResult Run(InspectionContext context);
}

/// <summary>What an inspection runs over. <see cref="Reader"/> is set when the snapshot has a typed reader.</summary>
public sealed record InspectionContext(
    HeapGraph Graph,
    SnapshotInfo Info,
    IObjectReader? Reader,
    IProgress<InspectionProgress>? Progress,
    CancellationToken CancellationToken)
{
    public void Report(string phase, long done, long total) => Progress?.Report(new InspectionProgress(phase, done, total));
}

public sealed record InspectionProgress(string Phase, long Done, long Total);

/// <summary>
/// An inspection's answer: how many findings, the bytes they cover, the bytes they waste (null when "waste" does not
/// apply, as for the large object heap's contents), a note on what could not be measured, and the rows.
/// </summary>
public sealed record InspectionResult(long Count, long Bytes, long? WastedBytes, string? Note, InspectionTable Rows);

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
    ];

    public static IInspection? Find(string name) => All.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
}
