using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.Wpf.ViewModels;

/// <summary>One row of the comparison grid, filled when its page arrives (one notification per row, as in the types grid).</summary>
public sealed class DiffRowItem(int index) : ObservableObject, IIndexedRow
{
    public int Index { get; } = index;

    public string Name { get; private set; } = string.Empty;

    /// <summary>"new", "gone" or empty.</summary>
    public string Change { get; private set; } = string.Empty;

    public long BaselineObjects { get; private set; }

    public long CurrentObjects { get; private set; }

    public long ObjectsDelta { get; private set; }

    public long BaselineBytes { get; private set; }

    public long CurrentBytes { get; private set; }

    public long BytesDelta { get; private set; }

    /// <summary>Distinct types (method tables) summed into the row in the baseline; 0 when it has none.</summary>
    public int BaselineTypes { get; private set; }

    /// <summary>Distinct types summed into the row in the current snapshot ("copies" when it differs from the baseline).</summary>
    public int CurrentTypes { get; private set; }

    /// <summary>The raw type name that opens the row's instances; null for namespace and assembly rows.</summary>
    public string? TypeName { get; private set; }

    /// <summary>The module file name of <see cref="TypeName"/> (empty when none).</summary>
    public string? Module { get; private set; }

    /// <summary>-1, 0 or 1: the sign of the bytes delta, for the growth color.</summary>
    public int Trend => Math.Sign(BytesDelta);

    public bool IsLoaded { get; private set; }

    /// <summary>A row list over a diff result set; fails at once when a column is missing.</summary>
    public static VirtualRowList<DiffRowItem> CreateList(ResultSetHandle? rows, ResultSetInfo info, Action<Exception>? onError = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        int[] layout =
        [
            ResultColumns.Require(info.Columns, TypeDiffColumns.Name),
            ResultColumns.Require(info.Columns, TypeDiffColumns.Change),
            ResultColumns.Require(info.Columns, TypeDiffColumns.BaselineObjects),
            ResultColumns.Require(info.Columns, TypeDiffColumns.CurrentObjects),
            ResultColumns.Require(info.Columns, TypeDiffColumns.ObjectsDelta),
            ResultColumns.Require(info.Columns, TypeDiffColumns.BaselineBytes),
            ResultColumns.Require(info.Columns, TypeDiffColumns.CurrentBytes),
            ResultColumns.Require(info.Columns, TypeDiffColumns.BytesDelta),
            ResultColumns.Find(info.Columns, TypeDiffColumns.BaselineTypes),
            ResultColumns.Find(info.Columns, TypeDiffColumns.CurrentTypes),
            ResultColumns.Find(info.Columns, TypeDiffColumns.TypeName),
            ResultColumns.Find(info.Columns, TypeDiffColumns.Module),
        ];
        return new VirtualRowList<DiffRowItem>(rows, info.TotalCount, i => new DiffRowItem(i), (row, cells) => row.Fill(cells, layout), onError, logger);
    }

    private void Fill(IReadOnlyList<string?> cells, int[] layout)
    {
        Name = Cells.Text(cells, layout[0]);
        Change = Cells.Text(cells, layout[1]);
        BaselineObjects = Cells.Long(cells, layout[2]);
        CurrentObjects = Cells.Long(cells, layout[3]);
        ObjectsDelta = Cells.Long(cells, layout[4]);
        BaselineBytes = Cells.Long(cells, layout[5]);
        CurrentBytes = Cells.Long(cells, layout[6]);
        BytesDelta = Cells.Long(cells, layout[7]);
        BaselineTypes = (int)Cells.Long(cells, layout[8]);
        CurrentTypes = (int)Cells.Long(cells, layout[9]);
        TypeName = layout[10] >= 0 ? Cells.Text(cells, layout[10]) : null;
        Module = layout[11] >= 0 ? Cells.Text(cells, layout[11]) : null;
        IsLoaded = true;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// The comparison grid (sprint 1.4, dotMemory's comparison view, ADR-018): two pickers over the open snapshots (the
/// earlier capture is the default baseline), per-type deltas, sorting by any column, the shared grouping and filter.
/// </summary>
public sealed partial class CompareViewModel : TypeGridViewModel<DiffRowItem>
{
    private readonly SnapshotSession _session;
    private bool _settingPair;

    [ObservableProperty]
    private SnapshotItemViewModel? _baseline;

    [ObservableProperty]
    private SnapshotItemViewModel? _current;


    public CompareViewModel(SnapshotSession session, ObservableCollection<SnapshotItemViewModel> snapshots, ILogger<CompareViewModel>? logger = null)
        : base(TypeDiffColumns.BytesDelta, logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        Snapshots.CollectionChanged += OnSnapshotsChanged;
        PickDefaults();
    }

    /// <summary>The open snapshots the pickers offer.</summary>
    public ObservableCollection<SnapshotItemViewModel> Snapshots { get; }


    /// <summary>Why there is nothing to compare yet, if so.</summary>
    public string? Hint => Snapshots.Count < 2
        ? "Open two snapshots to compare them: the earlier one is the baseline."
        : Baseline is null || Current is null ? "Pick a baseline and a current snapshot." : null;

    protected override bool CanQuery => Baseline is not null && Current is not null;

    protected override string BusyText => "Comparing...";

    partial void OnBaselineChanged(SnapshotItemViewModel? value)
    {
        if (!_settingPair)
        {
            OnPairChanged();
        }
    }

    partial void OnCurrentChanged(SnapshotItemViewModel? value)
    {
        if (!_settingPair)
        {
            OnPairChanged();
        }
    }

    protected override async Task<GridQuery<DiffRowItem>> RunQueryAsync(CancellationToken cancellationToken)
    {
        var groupBy = GroupBy;
        var diff = await _session.QueryTypeDiffAsync(Baseline!.Handle, Current!.Handle, groupBy, Filter, SortColumn, Descending, null, cancellationToken);
        var result = diff.Result;
        return new GridQuery<DiffRowItem>(
            diff.Rows,
            () => Totals = string.Create(
                CultureInfo.CurrentCulture,
                $"{result.Rows.TotalCount:N0} of {result.UnfilteredRows:N0} {Plural(groupBy)} ({result.NewRows:N0} new, {result.GoneRows:N0} gone); matching rows {Format.SignedBytes(result.MatchingBytesDelta)}; heap {Format.SignedBytes(result.CurrentBytes - result.BaselineBytes)}, {SignedCount(result.CurrentObjects - result.BaselineObjects)} objects (unreachable ones included{(result.ExactCounts ? string.Empty : "; estimated: a snapshot has sampled counts")})"),
            onError => DiffRowItem.CreateList(diff.Rows, result.Rows, onError, Logger));
    }

    /// <summary>Swaps baseline and current.</summary>
    [RelayCommand]
    private void Swap() => SetPair(Current, Baseline);


    private void OnPairChanged()
    {
        OnPropertyChanged(nameof(Hint));
        if (CanQuery)
        {
            Requery();
        }
        else
        {
            ClearResults();
        }
    }

    private void OnSnapshotsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A picked snapshot that was closed leaves its picker; an empty picker takes another open snapshot.
        if (Baseline is not null && !Snapshots.Contains(Baseline))
        {
            Baseline = null;
        }

        if (Current is not null && !Snapshots.Contains(Current))
        {
            Current = null;
        }

        PickDefaults();
        OnPropertyChanged(nameof(Hint));
    }

    private void PickDefaults()
    {
        if ((Baseline is not null && Current is not null) || Snapshots.Count < 2)
        {
            return;
        }

        // By capture time, open order breaking ties; snapshots without a capture time count as the newest.
        var ordered = Snapshots
            .Select((s, i) => (Item: s, Order: i))
            .OrderBy(x => x.Item.Handle.Descriptor.CapturedAtUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.Order)
            .Select(x => x.Item)
            .ToList();
        if (Baseline is null && Current is null)
        {
            // The two most recent captures, the earlier one as the baseline.
            SetPair(ordered[^2], ordered[^1]);
        }
        else if (Baseline is null)
        {
            // The latest capture before the current one, else the earliest other snapshot.
            var others = ordered.Where(s => s != Current).ToList();
            var position = ordered.IndexOf(Current!);
            SetPair(others.LastOrDefault(s => ordered.IndexOf(s) < position) ?? others[0], Current);
        }
        else
        {
            // The first capture after the baseline, else the latest other snapshot.
            var others = ordered.Where(s => s != Baseline).ToList();
            var position = ordered.IndexOf(Baseline);
            SetPair(Baseline, others.FirstOrDefault(s => ordered.IndexOf(s) > position) ?? others[^1]);
        }
    }

    /// <summary>Sets both pickers and queries once.</summary>
    private void SetPair(SnapshotItemViewModel? baseline, SnapshotItemViewModel? current)
    {
        _settingPair = true;
        try
        {
            Baseline = baseline;
            Current = current;
        }
        finally
        {
            _settingPair = false;
        }

        OnPairChanged();
    }

    private static string SignedCount(long value) => value.ToString("+#,0;-#,0;0", CultureInfo.CurrentCulture);
}
