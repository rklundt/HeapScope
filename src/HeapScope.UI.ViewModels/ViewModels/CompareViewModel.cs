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

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;

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


    public CompareViewModel(SnapshotSession session, ObservableCollection<SnapshotItemViewModel> snapshots, IUiDispatcher dispatcher, ILogger<CompareViewModel>? logger = null, INavigator? navigator = null)
        : base(TypeDiffColumns.BytesDelta, dispatcher, logger)
    {
        OpenRowCommand = new RelayCommand<DiffRowItem?>(row => OpenRow(row, navigator));
        ShowInRetainersCommand = new RelayCommand<DiffRowItem?>(row => Link(row, navigator, (s, r) => NavigationTarget.Retainers(s, typeName: r.TypeName, module: r.Module)));
        RetainedBySetCommand = new RelayCommand<DiffRowItem?>(row => Link(row, navigator, (s, r) => NavigationTarget.RetainedBySet(s, r.TypeName!, r.Module)));
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
        : Baseline is null || Current is null ? "Pick a baseline and a current snapshot."
        : ByOpenOrder ? "A Linux or macOS core dump records no capture time (its file's time is shown), so the snapshot opened first is the baseline. Swap if that is the later one."
        : null;

    // Sprint 2.3.6 (2.3.5 review M4): a capture time that is only the file's time can be changed by a copy, so it does not
    // decide which snapshot is the baseline.
    private bool ByOpenOrder => Snapshots.Any(s => s.Handle.Descriptor.CaptureTimeFromFile);

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
        var scope = Scope(Baseline!.Handle.Descriptor.Kind, Current!.Handle.Descriptor.Kind);
        var diff = await _session.QueryTypeDiffAsync(Baseline!.Handle, Current!.Handle, groupBy, Filter, SortColumn, Descending, null, cancellationToken);
        var result = diff.Result;
        return new GridQuery<DiffRowItem>(
            diff.Rows,
            () => Totals = string.Create(
                CultureInfo.CurrentCulture,
                $"{result.Rows.TotalCount:N0} of {result.UnfilteredRows:N0} {Plural(groupBy)} ({result.NewRows:N0} new, {result.GoneRows:N0} gone); matching rows {Format.SignedBytes(result.MatchingBytesDelta)}; heap {Format.SignedBytes(result.CurrentBytes - result.BaselineBytes)}, {SignedCount(result.CurrentObjects - result.BaselineObjects)} objects ({scope}{(result.ExactCounts ? string.Empty : "; estimated: a snapshot is sampled or incomplete")})"),
            onError => DiffRowItem.CreateList(diff.Rows, result.Rows, onError, Logger));
    }

    // What the totals count (sprint 1.8 review): a full dump holds dead objects not yet collected, a gcdump only the
    // survivors of the collection its capture ran, so a full dump against a gcdump shows garbage as a drop.
    private static string Scope(SnapshotKindDto baseline, SnapshotKindDto current) => (baseline, current) switch
    {
        (SnapshotKindDto.FullDump, SnapshotKindDto.FullDump) => "unreachable ones included",
        (SnapshotKindDto.GcDump, SnapshotKindDto.GcDump) => "survivors of each gcdump's collection",
        _ => "a full dump against a gcdump: the dump counts unreachable objects the gcdump's collection removed, and nested types are matched by the gcdump's shorter names",
    };

    /// <summary>Swaps baseline and current.</summary>
    [RelayCommand]
    private void Swap() => SetPair(Current, Baseline, "swapped by the user");

    /// <summary>A diff row's group in the current snapshot's dominator tree (sprint 2.4).</summary>
    public System.Windows.Input.ICommand ShowInRetainersCommand { get; }

    /// <summary>What a diff row's objects in the current snapshot retain together (sprint 2.4).</summary>
    public System.Windows.Input.ICommand RetainedBySetCommand { get; }

    // Retention is the current snapshot's: a type it no longer has retains nothing there.
    private void Link(DiffRowItem? row, INavigator? navigator, Func<SnapshotHandle, DiffRowItem, NavigationTarget> target)
    {
        if (row is not { IsLoaded: true } || Current is not { } current)
        {
            return;
        }

        if (row.TypeName is null || row.CurrentObjects == 0)
        {
            Status = row.TypeName is null ? "Group by type to follow a row's objects." : "The current snapshot has no objects of this type.";
            return;
        }

        navigator?.Navigate(target(current.Handle, row));
    }

    /// <summary>
    /// Opens a row's type on the Snapshot tab: its objects in the current snapshot, or in the baseline for a type the
    /// current snapshot no longer has (ADR-018; sprint 1.5 review).
    /// </summary>
    private void OpenRow(DiffRowItem? row, INavigator? navigator)
    {
        if (row is not { IsLoaded: true } || Current is not { } current)
        {
            return;
        }

        if (row.TypeName is null)
        {
            // In Type grouping a row has no key only when one side merged several of its types under one name: a full
            // dump's nested types compared with a gcdump, which records them without their declaring type (sprint 1.8).
            Status = GroupBy == TypeGrouping.Type
                ? "This row joins several types of the full dump under the one name the gcdump records, so it cannot open one type's objects. Find the type in the full dump's own summary."
                : "Group by type to open a row's objects.";
            return;
        }

        var side = row.CurrentObjects == 0 && Baseline is { } baseline ? baseline : current;
        navigator?.Navigate(NavigationTarget.Instances(side.Handle, row.TypeName, row.Module));
    }

    // How the pair was chosen, for the log line that backs up a disputed comparison (sprint 2.3.7 review H3).
    private void OnPairChanged(string reason = "picked by the user")
    {
        OnPropertyChanged(nameof(Hint));
        if (Baseline is { } baseline && Current is { } current)
        {
            Logger.LogInformation(
                "Compare pair {Reason}: baseline {BaselinePath} (worker snapshot {BaselineId}, captured {BaselineCaptured:O}{BaselineFileTime}), current {CurrentPath} (worker snapshot {CurrentId}, captured {CurrentCaptured:O}{CurrentFileTime})",
                reason,
                baseline.Handle.Path,
                baseline.Handle.Descriptor.SnapshotId,
                baseline.Handle.Descriptor.CapturedAtUtc,
                baseline.Handle.Descriptor.CaptureTimeFromFile ? ", the file's time" : string.Empty,
                current.Handle.Path,
                current.Handle.Descriptor.SnapshotId,
                current.Handle.Descriptor.CapturedAtUtc,
                current.Handle.Descriptor.CaptureTimeFromFile ? ", the file's time" : string.Empty);
        }

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

        // By capture time, open order breaking ties; snapshots without a capture time count as the newest. Open order alone
        // when a capture time is only a file's time.
        var ordered = Snapshots
            .Select((s, i) => (Item: s, Order: i))
            .OrderBy(x => ByOpenOrder ? DateTimeOffset.MinValue : x.Item.Handle.Descriptor.CapturedAtUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.Order)
            .Select(x => x.Item)
            .ToList();
        if (Baseline is null && Current is null)
        {
            // The two most recent captures, the earlier one as the baseline.
            SetPair(ordered[^2], ordered[^1], Automatic);
        }
        else if (Baseline is null)
        {
            // The latest capture before the current one, else the earliest other snapshot.
            var others = ordered.Where(s => s != Current).ToList();
            var position = ordered.IndexOf(Current!);
            SetPair(others.LastOrDefault(s => ordered.IndexOf(s) < position) ?? others[0], Current, Automatic);
        }
        else
        {
            // The first capture after the baseline, else the latest other snapshot.
            var others = ordered.Where(s => s != Baseline).ToList();
            var position = ordered.IndexOf(Baseline);
            SetPair(Baseline, others.FirstOrDefault(s => ordered.IndexOf(s) > position) ?? others[^1], Automatic);
        }
    }

    private string Automatic => ByOpenOrder ? "chosen automatically by open order (a capture time is only a file's time)" : "chosen automatically by capture time";

    /// <summary>Sets both pickers and queries once.</summary>
    private void SetPair(SnapshotItemViewModel? baseline, SnapshotItemViewModel? current, string reason)
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

        OnPairChanged(reason);
    }

    private static string SignedCount(long value) => value.ToString("+#,0;-#,0;0", CultureInfo.CurrentCulture);
}
