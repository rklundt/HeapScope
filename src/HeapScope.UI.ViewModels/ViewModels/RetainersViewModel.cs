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

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;

/// <summary>
/// The Retainers tab (sprint 2.4, dotMemory's dominators view): the biggest retainers as the top of the dominator tree,
/// expanded in place (a click on the arrow, Enter, Right and Left), children as objects or grouped by type or namespace;
/// a focus with its dominators as a trail; the icicle chart of the focus; and the objects a type's objects retain
/// together. Like the inspections, nothing is computed until the tab is shown, since the first query of a snapshot computes
/// its dominators. The tree is re-queried on every expansion (the worker flattens it; the keys survive re-runs).
/// </summary>
public sealed partial class RetainersViewModel : GridQueryViewModel<DominatorRowItem>
{
    private readonly SnapshotSession _session;
    private readonly INavigator? _navigator;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private CancellationTokenSource? _icicle;
    private bool _queried;
    private bool _syncingSelection;
    private string? _selectKey;

    [ObservableProperty]
    private SnapshotHandle? _snapshot;

    /// <summary>Whether the tab is shown; the first time for a snapshot, its tree is queried.</summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private DominatorGroupingDto _grouping;

    /// <summary>The focus object's address; 0 for the biggest retainers.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFocus))]
    private ulong _focusAddress;

    /// <summary>The focus's dominators from the top, for the trail.</summary>
    [ObservableProperty]
    private IReadOnlyList<DominatorAncestorDto> _ancestors = [];

    /// <summary>What the rows stand for, the totals and how the dominators were obtained.</summary>
    [ObservableProperty]
    private string? _note;

    [ObservableProperty]
    private DominatorRowItem? _selectedRow;

    [ObservableProperty]
    private IReadOnlyList<IcicleSegmentDto> _icicleSegments = [];

    [ObservableProperty]
    private long _icicleTotal;

    [ObservableProperty]
    private IcicleSegmentDto? _selectedSegment;

    [ObservableProperty]
    private string? _icicleStatus;

    public RetainersViewModel(SnapshotSession session, IUiDispatcher dispatcher, ILogger<RetainersViewModel>? logger, INavigator? navigator)
        : base(DominatorTreeColumns.Retained, dispatcher, logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _navigator = navigator;
        Set = new RetainedBySetViewModel(session, dispatcher, logger, navigator);
        OpenRowCommand = ToggleCommand;
    }

    public static IReadOnlyList<DominatorGroupingDto> Groupings { get; } = Enum.GetValues<DominatorGroupingDto>();

    public bool HasFocus => FocusAddress != 0;

    /// <summary>"Objects retained by this set" for the type last asked for.</summary>
    public RetainedBySetViewModel Set { get; }

    /// <summary>The icicle query in progress (tests and smoke runs await it).</summary>
    public Task IcicleTask { get; private set; } = Task.CompletedTask;

    /// <summary>The expanded rows' keys (tests).</summary>
    public IReadOnlyCollection<string> Expanded => _expanded;

    protected override bool CanQuery => Snapshot is not null && IsActive;

    protected override string BusyText => "Listing the biggest retainers (the first time for a snapshot computes its dominators)...";

    protected override string EmptyText => "No root reaches anything in this snapshot.";

    /// <summary>Shows <paramref name="snapshot"/>'s biggest retainers (another snapshot was selected); queried when the tab is shown.</summary>
    public void Show(SnapshotHandle? snapshot)
    {
        ClearResults();
        Set.Show(snapshot, null, null);
        _expanded.Clear();
        _queried = false;
        Snapshot = snapshot;
        FocusAddress = 0;
        Ancestors = [];
        Start();
    }

    /// <summary>Follows a link to this tab: a focus object, a type's group, or a type's set.</summary>
    public void Navigate(NavigationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Snapshot != Snapshot)
        {
            Show(target.Snapshot);
        }

        if (target.Kind == NavigationKind.RetainedBySet && target.TypeName is { } setType)
        {
            Set.Show(target.Snapshot, setType, target.Module);
            Set.Start();
            Start();
            return;
        }

        if (target.Address != 0)
        {
            FocusOn(target.Address);
        }
        else if (target.NodeKey is { } groupKey)
        {
            // A type's group at the top level: group by type and open it.
            FocusAddress = 0;
            _expanded.Add(groupKey);
            _selectKey = groupKey;
            _queried = false;
            Grouping = DominatorGroupingDto.Type; // Re-queries if it changed.
        }

        Start();
    }

    partial void OnIsActiveChanged(bool value) => Start();

    partial void OnGroupingChanged(DominatorGroupingDto value) => RequeryTree();

    partial void OnSelectedRowChanged(DominatorRowItem? value)
    {
        if (_syncingSelection || value is null)
        {
            return;
        }

        _syncingSelection = true;
        SelectedSegment = IcicleSegments.FirstOrDefault(s => s.Key == value.Key);
        _syncingSelection = false;
    }

    partial void OnSelectedSegmentChanged(IcicleSegmentDto? value)
    {
        if (_syncingSelection || value is null)
        {
            return;
        }

        // A bar selects its row when the row is in the loaded part of the tree.
        _syncingSelection = true;
        SelectedRow = FindLoadedRow(value.Key) ?? SelectedRow;
        _syncingSelection = false;
    }

    protected override async Task<GridQuery<DominatorRowItem>> RunQueryAsync(CancellationToken cancellationToken)
    {
        var finished = false;
        var progress = new Progress<JobProgress>(p => Status = finished || cancellationToken.IsCancellationRequested ? Status : ReferrersText.Progress(p));
        var answer = await _session.QueryDominatorTreeAsync(Snapshot!, [.. _expanded], Grouping, FocusAddress, progress, cancellationToken);
        finished = true;
        var result = answer.Result;
        var select = _selectKey;
        return new GridQuery<DominatorRowItem>(
            answer.Rows,
            () =>
            {
                Ancestors = result.Ancestors;
                Totals = HasFocus
                    ? string.Create(CultureInfo.CurrentCulture, $"Retains {Format.Bytes(result.FocusRetained)} of {Format.Bytes(result.TotalRetained)} reachable")
                    : string.Create(CultureInfo.CurrentCulture, $"{result.TopLevel:N0} top-level retainers of {Format.Bytes(result.TotalRetained)} reachable");
                Note = RetainedText.Source(result.Dominators) + " " + RetainersText.Exact
                    + (result.Truncated ? " " + RetainersText.Truncated : string.Empty);
                _selectKey = null;
                if (select is not null)
                {
                    _ = SelectWhenLoadedAsync(select);
                }
            },
            onError => DominatorRowItem.CreateList(answer.Rows, result.Rows, onError, Logger));
    }

    /// <summary>Expands a collapsed row, or collapses an expanded one (a click on its arrow, Enter, a double-click).</summary>
    [RelayCommand]
    private void Toggle(DominatorRowItem? row)
    {
        if (row is { CanExpand: true })
        {
            SetExpanded(row, !row.IsExpanded);
        }
    }

    [RelayCommand]
    private void Expand(DominatorRowItem? row)
    {
        if (row is { CanExpand: true, IsExpanded: false })
        {
            SetExpanded(row, true);
        }
    }

    [RelayCommand]
    private void Collapse(DominatorRowItem? row)
    {
        if (row is { IsExpanded: true } && row.Key != FocusKey)
        {
            SetExpanded(row, false);
        }
    }

    /// <summary>Shows the tree below an object row, with its dominators as the trail.</summary>
    [RelayCommand]
    private void Focus(DominatorRowItem? row)
    {
        if (row is { Address: not 0 })
        {
            FocusOn(row.Address);
        }
    }

    [RelayCommand]
    private void FocusSegment(IcicleSegmentDto? segment)
    {
        if (segment is not null)
        {
            FocusOn(segment.Address);
        }
    }

    [RelayCommand]
    private void FocusAncestor(DominatorAncestorDto? ancestor)
    {
        if (ancestor is not null)
        {
            FocusOn(ancestor.Address);
        }
    }

    /// <summary>Back to the biggest retainers.</summary>
    [RelayCommand]
    private void ShowTop() => FocusOn(0);

    [RelayCommand]
    private void OpenObject(DominatorRowItem? row)
    {
        if (row is { Address: not 0 } && Snapshot is { } snapshot)
        {
            _navigator?.Navigate(NavigationTarget.Object(snapshot, row.Address));
        }
    }

    [RelayCommand]
    private void OpenInstances(DominatorRowItem? row)
    {
        if (row?.TypeName is { } typeName && Snapshot is { } snapshot)
        {
            _navigator?.Navigate(NavigationTarget.Instances(snapshot, typeName, row.Module));
        }
    }

    [RelayCommand]
    private void OpenPaths(DominatorRowItem? row)
    {
        if (row is { Address: not 0 } && Snapshot is { } snapshot)
        {
            _navigator?.Navigate(NavigationTarget.Object(snapshot, row.Address, ObjectReferenceTabs.Retention));
        }
    }

    [RelayCommand]
    private void RetainedBySet(DominatorRowItem? row)
    {
        if (row?.TypeName is { } typeName && Snapshot is { } snapshot)
        {
            Set.Show(snapshot, typeName, row.Module);
            Set.Start();
        }
    }

    private string? FocusKey => HasFocus ? DominatorTreeKeys.Object(FocusAddress) : null;

    private void FocusOn(ulong address)
    {
        FocusAddress = address;
        _expanded.Clear();
        _queried = false;
        Start();
    }

    private void SetExpanded(DominatorRowItem row, bool expanded)
    {
        if (expanded)
        {
            _expanded.Add(row.Key);
        }
        else
        {
            _expanded.Remove(row.Key);
        }

        _selectKey = row.Key;
        RequeryTree();
    }

    private void RequeryTree()
    {
        if (_queried)
        {
            Requery();
        }
        else
        {
            Start();
        }
    }

    /// <summary>Queries the tree and the icicle once per snapshot and focus, when the tab is shown.</summary>
    private void Start()
    {
        if (!CanQuery || _queried)
        {
            return;
        }

        _queried = true;
        Requery();
        LoadIcicle();
        Set.Start();
    }

    private void LoadIcicle()
    {
        _icicle?.Cancel();
        _icicle?.Dispose();
        _icicle = new CancellationTokenSource();
        IcicleTask = LoadIcicleAsync(Snapshot!, FocusAddress, _icicle.Token);
    }

    private async Task LoadIcicleAsync(SnapshotHandle snapshot, ulong focus, CancellationToken cancellationToken)
    {
        IcicleStatus = "Drawing...";
        try
        {
            var result = await _session.QueryIcicleAsync(snapshot, focus, null, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            IcicleTotal = result.Total;
            IcicleSegments = result.Segments;
            SelectedSegment = null;
            IcicleStatus = null;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // A newer focus replaced this chart.
        }
        catch (Exception ex) when (ex is WorkerException or OperationCanceledException)
        {
            IcicleStatus = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The icicle chart failed");
            IcicleStatus = "The chart failed. Details are in the log.";
        }
    }

    private async Task SelectWhenLoadedAsync(string key)
    {
        await Rows.WhenLoadedAsync();
        if (FindLoadedRow(key) is { } row)
        {
            SelectedRow = row;
        }
    }

    private DominatorRowItem? FindLoadedRow(string key)
    {
        foreach (var index in Rows.RequestedPages.SelectMany(p => Enumerable.Range(p * VirtualRowList.PageSize, VirtualRowList.PageSize)))
        {
            if (index < Rows.Count && Rows.GetRow(index) is { IsLoaded: true } row && row.Key == key)
            {
                return row;
            }
        }

        return null;
    }

    protected override void OnCleared()
    {
        _icicle?.Cancel();
        IcicleSegments = [];
        IcicleTotal = 0;
        SelectedSegment = null;
        IcicleStatus = null;
        Note = null;
    }
}

/// <summary>Texts of the Retainers tab (sprint 2.4).</summary>
public static class RetainersText
{
    public const string Exact = "A row's retained bytes are what it alone keeps alive, exactly; siblings never share, so a group's bytes are the sum of its members'.";

    public const string Truncated = "The tree is cut at 50,000 rows; collapse rows or focus on one to see the rest.";
}

/// <summary>A row of the dominator tree grid (sprint 2.4), filled from the worker's columns.</summary>
public sealed class DominatorRowItem(int index) : ObservableObject, IIndexedRow
{
    /// <summary>Pixels of indentation per tree level.</summary>
    public const double IndentPerLevel = 16;

    public int Index { get; } = index;

    public bool IsLoaded { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string ParentKey { get; private set; } = string.Empty;

    public int Depth { get; private set; }

    /// <summary><c>Object</c>, <c>Group</c> or <c>More</c>.</summary>
    public string Kind { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Module { get; private set; }

    public ulong Address { get; private set; }

    public string AddressText => Address == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"0x{Address:x}");

    public long Objects { get; private set; }

    public long Retained { get; private set; }

    public long Bytes { get; private set; }

    public double Percent { get; private set; }

    public int Children { get; private set; }

    public bool IsExpanded { get; private set; }

    public bool IsObject => Kind == "Object";

    /// <summary>The type an object row or a type group stands for (a namespace group or a "more" row has none).</summary>
    public string? TypeName => IsObject || (Kind == "Group" && Module is not null) ? Name : null;

    /// <summary>A "more" row or a node with children expands.</summary>
    public bool CanExpand => Kind == "More" || Children > 0;

    public double Indent => Depth * IndentPerLevel;

    /// <summary>The arrow: open, closed, or none for a leaf.</summary>
    public string Glyph => !CanExpand ? string.Empty : IsExpanded ? "▾" : "▸";

    public static VirtualRowList<DominatorRowItem> CreateList(ResultSetHandle? rows, ResultSetInfo info, Action<Exception>? onError = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        var at = info.Columns.Select((c, i) => (c.Name, i)).ToDictionary(c => c.Name, c => c.i, StringComparer.Ordinal);
        return new VirtualRowList<DominatorRowItem>(rows, info.TotalCount, i => new DominatorRowItem(i), (row, cells) => row.Fill(cells, at), onError, logger);
    }

    private void Fill(IReadOnlyList<string?> cells, Dictionary<string, int> at)
    {
        string? Cell(string column) => at.TryGetValue(column, out var i) && i < cells.Count ? cells[i] : null;
        long Number(string column) => long.TryParse(Cell(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        Key = Cell(DominatorTreeColumns.Key) ?? string.Empty;
        ParentKey = Cell(DominatorTreeColumns.ParentKey) ?? string.Empty;
        Depth = (int)Number(DominatorTreeColumns.Depth);
        Kind = Cell(DominatorTreeColumns.Kind) ?? string.Empty;
        Name = Cell(DominatorTreeColumns.Name) ?? string.Empty;
        Module = Cell(DominatorTreeColumns.Module);
        Address = Cell(DominatorTreeColumns.Address) is { Length: > 2 } address
            && ulong.TryParse(address.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) ? a : 0;
        Objects = Number(DominatorTreeColumns.Objects);
        Retained = Number(DominatorTreeColumns.Retained);
        Bytes = Number(DominatorTreeColumns.Bytes);
        Percent = double.TryParse(Cell(DominatorTreeColumns.Percent), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
        Children = (int)Number(DominatorTreeColumns.Children);
        IsExpanded = Cell(DominatorTreeColumns.Expanded) == "1";
        IsLoaded = true;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// "Objects retained by this set" (sprint 2.4): what every object of a type keeps alive together, exactly, by type, next
/// to the type summary's lower bound. A row opens its type's objects.
/// </summary>
public sealed partial class RetainedBySetViewModel : GridQueryViewModel<CellRowItem>
{
    private readonly SnapshotSession _session;
    private bool _queried;

    [ObservableProperty]
    private SnapshotHandle? _snapshot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    private string? _typeName;

    [ObservableProperty]
    private string? _module;

    /// <summary>The totals: the set, what it retains, and the lower bound.</summary>
    [ObservableProperty]
    private string? _summary;

    /// <summary>The visible columns with their index in the cells.</summary>
    [ObservableProperty]
    private IReadOnlyList<(ResultColumn Column, int Index)> _columns = [];

    public RetainedBySetViewModel(SnapshotSession session, IUiDispatcher dispatcher, ILogger? logger, INavigator? navigator)
        : base(RetainedBySetColumns.Retained, dispatcher, logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        OpenRowCommand = new RelayCommand<CellRowItem?>(row =>
        {
            if (row?[RetainedBySetColumns.TypeName] is { } typeName && Snapshot is { } snapshot)
            {
                navigator?.Navigate(NavigationTarget.Instances(snapshot, typeName, row[RetainedBySetColumns.Module]));
            }
        });
    }

    public bool IsShown => TypeName is not null;

    protected override bool CanQuery => Snapshot is not null && TypeName is not null;

    protected override string BusyText => "Finding what the objects retain together...";

    protected override string EmptyText => "None of these objects is reachable, so they retain nothing.";

    /// <summary>Asks for <paramref name="typeName"/>'s set (null hides the panel); <see cref="Start"/> queries it.</summary>
    public void Show(SnapshotHandle? snapshot, string? typeName, string? module)
    {
        ClearResults();
        Snapshot = snapshot;
        TypeName = typeName;
        Module = module;
        _queried = false;
    }

    /// <summary>Queries the set once.</summary>
    public void Start()
    {
        if (CanQuery && !_queried)
        {
            _queried = true;
            Requery();
        }
    }

    [RelayCommand]
    private void Hide() => Show(Snapshot, null, null);

    protected override async Task<GridQuery<CellRowItem>> RunQueryAsync(CancellationToken cancellationToken)
    {
        var finished = false;
        var progress = new Progress<JobProgress>(p => Status = finished || cancellationToken.IsCancellationRequested ? Status : ReferrersText.Progress(p));
        var answer = await _session.QueryRetainedBySetAsync(Snapshot!, TypeName!, Module, progress, cancellationToken);
        finished = true;
        var result = answer.Result;
        return new GridQuery<CellRowItem>(
            answer.Rows,
            () =>
            {
                Columns = [.. result.Types.Columns.Select((c, i) => (c, i)).Where(c => c.c.Name is not RetainedBySetColumns.TypeName and not RetainedBySetColumns.Module)];
                Summary = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{result.SetObjects:N0} objects of {Format.Bytes(result.SetBytes)} retain {result.RetainedObjects:N0} objects, {Format.Bytes(result.RetainedBytes)} exactly ({100.0 * result.RetainedBytes / Math.Max(1, result.TotalRetained):N1}% of the reachable heap). The types grid shows at least {Format.Bytes(result.LowerBoundBytes)}: it leaves out what several of them hold together.");
                Totals = string.Create(CultureInfo.CurrentCulture, $"{result.Types.TotalCount:N0} types, {result.Seconds:N1} s");
            },
            onError => CellRowItem.CreateList(answer.Rows, result.Types, onError, Logger));
    }
}
