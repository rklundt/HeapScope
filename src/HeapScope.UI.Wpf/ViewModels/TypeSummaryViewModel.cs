using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.Wpf.ViewModels;

/// <summary>
/// Where each value of a type summary row is, found from the result set's column names, so a change in the worker's
/// column order cannot put values under the wrong header. Name, Objects and Bytes are required.
/// </summary>
public sealed class TypeRowLayout
{
    private TypeRowLayout(IReadOnlyList<ResultColumn> columns)
    {
        Name = ResultColumns.Require(columns, "Name");
        Objects = ResultColumns.Require(columns, "Objects");
        Bytes = ResultColumns.Require(columns, "Bytes");
        Percent = ResultColumns.Find(columns, "Percent");
        Gen0 = ResultColumns.Find(columns, "Gen0");
        Gen1 = ResultColumns.Find(columns, "Gen1");
        Gen2 = ResultColumns.Find(columns, "Gen2");
        LargeObjectHeap = ResultColumns.Find(columns, "LargeObjectHeap");
        PinnedObjectHeap = ResultColumns.Find(columns, "PinnedObjectHeap");
        Frozen = ResultColumns.Find(columns, "Frozen");
    }

    public int Name { get; }

    public int Objects { get; }

    public int Bytes { get; }

    public int Percent { get; }

    public int Gen0 { get; }

    public int Gen1 { get; }

    public int Gen2 { get; }

    public int LargeObjectHeap { get; }

    public int PinnedObjectHeap { get; }

    public int Frozen { get; }

    public static TypeRowLayout From(IReadOnlyList<ResultColumn> columns) => new(columns ?? throw new ArgumentNullException(nameof(columns)));
}

/// <summary>
/// One row of the types grid, filled when its page arrives (data virtualization). Values are set together and announced
/// with one "all properties changed" notification: a page fills 200 rows, and a notification per value made the grid's
/// rebinding exceed the 100 ms dispatcher budget (sprint 1.3 smoke run).
/// </summary>
public sealed class TypeRowItem(int index) : ObservableObject, IIndexedRow
{
    public int Index { get; } = index;

    public string Name { get; private set; } = string.Empty;

    public long Objects { get; private set; }

    public long Bytes { get; private set; }

    public double Percent { get; private set; }

    public long Gen0 { get; private set; }

    public long Gen1 { get; private set; }

    public long Gen2 { get; private set; }

    public long LargeObjectHeap { get; private set; }

    public long PinnedObjectHeap { get; private set; }

    public long Frozen { get; private set; }

    public bool IsLoaded { get; private set; }

    /// <summary>A row list over a type summary result set; fails at once when a required column is missing.</summary>
    public static VirtualRowList<TypeRowItem> CreateList(ResultSetHandle? rows, ResultSetInfo info, Action<Exception>? onError = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        var layout = TypeRowLayout.From(info.Columns);
        return new VirtualRowList<TypeRowItem>(rows, info.TotalCount, i => new TypeRowItem(i), (row, cells) => row.Fill(cells, layout), onError, logger);
    }

    internal void Fill(IReadOnlyList<string?> cells, TypeRowLayout layout)
    {
        Name = Cells.Text(cells, layout.Name);
        Objects = Cells.Long(cells, layout.Objects);
        Bytes = Cells.Long(cells, layout.Bytes);
        Percent = Cells.Double(cells, layout.Percent);
        Gen0 = Cells.Long(cells, layout.Gen0);
        Gen1 = Cells.Long(cells, layout.Gen1);
        Gen2 = Cells.Long(cells, layout.Gen2);
        LargeObjectHeap = Cells.Long(cells, layout.LargeObjectHeap);
        PinnedObjectHeap = Cells.Long(cells, layout.PinnedObjectHeap);
        Frozen = Cells.Long(cells, layout.Frozen);
        IsLoaded = true;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Invariant-culture cell parsing; a missing column (index -1) or empty cell reads as zero or empty.</summary>
internal static class Cells
{
    public static string Text(IReadOnlyList<string?> cells, int index) => index >= 0 && index < cells.Count ? cells[index] ?? string.Empty : string.Empty;

    public static long Long(IReadOnlyList<string?> cells, int index) =>
        index >= 0 && index < cells.Count && long.TryParse(cells[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    public static double Double(IReadOnlyList<string?> cells, int index) =>
        index >= 0 && index < cells.Count && double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

/// <summary>A heap kind in the overview bar: share of the snapshot's bytes, in its fixed color.</summary>
public sealed record HeapKindShare(HeapKindDto Kind, string Label, long Objects, long Bytes, double Fraction)
{
    public string BrushKey => "HeapKind." + Kind;

    public string Text => string.Create(CultureInfo.CurrentCulture, $"{Label} {Format.Bytes(Bytes)} ({Fraction * 100:N1}%)");
}

/// <summary>
/// The types grid of a snapshot (sprint 1.3, dotMemory's types view): grouping, type-to-filter (debounced) and sorting
/// re-query the worker; rows are virtualized; generation columns show only when the snapshot has generations.
/// </summary>
public sealed partial class TypeSummaryViewModel : GridQueryViewModel<TypeRowItem>
{
    private readonly SnapshotSession _session;
    private SnapshotHandle? _snapshot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortState))]
    private TypeSummarySort _sort = TypeSummarySort.Bytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortState))]
    private bool _descending = true;

    [ObservableProperty]
    private bool _hasGenerations = true;

    [ObservableProperty]
    private string? _generationsExplanation;

    [ObservableProperty]
    private IReadOnlyList<HeapKindShare> _heapKinds = [];

    public TypeSummaryViewModel(SnapshotSession session, ILogger<TypeSummaryViewModel>? logger = null)
        : base(logger) => _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>
    /// The sorted column's <c>SortMemberPath</c> and direction ("Bytes|desc"); the grid shows its header arrow from this,
    /// so the arrow always matches the order the worker used.
    /// </summary>
    public string SortState => (Sort switch
    {
        TypeSummarySort.Name => nameof(TypeRowItem.Name),
        TypeSummarySort.Count => nameof(TypeRowItem.Objects),
        _ => nameof(TypeRowItem.Bytes),
    }) + (Descending ? "|desc" : "|asc");

    protected override bool CanQuery => _snapshot is not null;

    protected override string BusyText => "Summarizing...";

    /// <summary>Shows a snapshot (or nothing) and queries it with the current grouping, filter and sort.</summary>
    public void Show(SnapshotHandle? snapshot)
    {
        _snapshot = snapshot;
        if (snapshot is null)
        {
            ClearResults();
            return;
        }

        Requery();
    }

    protected override void OnCleared() => HeapKinds = [];

    protected override async Task<GridQuery<TypeRowItem>> RunQueryAsync(CancellationToken cancellationToken)
    {
        var snapshot = _snapshot!;
        var groupBy = GroupBy;
        var summary = await _session.QueryTypeSummaryAsync(snapshot, groupBy, Filter, Sort, Descending, null, cancellationToken);
        var result = summary.Result;
        return new GridQuery<TypeRowItem>(
            summary.Rows,
            () =>
            {
                HasGenerations = result.HasGenerations;
                GenerationsExplanation = result.HasGenerations
                    ? null
                    : CapabilityDescriptions.ExplainMissing(SnapshotFeatures.Generations, snapshot.Descriptor.Kind);
                HeapKinds = [.. result.HeapKinds.Select(k => new HeapKindShare(k.Kind, Label(k.Kind), k.Objects, k.Bytes, result.TotalBytes == 0 ? 0 : (double)k.Bytes / result.TotalBytes))];
                Totals = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{result.Rows.TotalCount:N0} of {result.UnfilteredRows:N0} {Plural(groupBy)}; {result.TotalObjects:N0} objects, {Format.Bytes(result.TotalBytes)} (all objects in the dump, unreachable ones included)");
            },
            onError => TypeRowItem.CreateList(summary.Rows, result.Rows, onError, Logger));
    }

    /// <summary>Sorts by a grid column: a second click on the same column flips the direction.</summary>
    [RelayCommand]
    private void SortBy(string? column)
    {
        var sort = column switch
        {
            nameof(TypeRowItem.Name) => TypeSummarySort.Name,
            nameof(TypeRowItem.Objects) => TypeSummarySort.Count,
            _ => TypeSummarySort.Bytes,
        };

        if (sort == Sort)
        {
            Descending = !Descending;
        }
        else
        {
            Sort = sort;
            Descending = sort != TypeSummarySort.Name;
        }

        Requery();
    }

    private static string Label(HeapKindDto kind) => kind switch
    {
        HeapKindDto.LargeObjectHeap => "LOH",
        HeapKindDto.PinnedObjectHeap => "POH",
        HeapKindDto.Unknown => "Unknown generation",
        _ => kind.ToString(),
    };
}
