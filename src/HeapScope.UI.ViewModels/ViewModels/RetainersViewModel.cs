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

using System.Diagnostics;
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
/// its dominators. The tree is re-queried on every expansion (the worker flattens it; the keys carry the state), and the
/// worker says where the row just toggled or linked to landed, so it stays selected and in view (the 2.4 review).
/// Every user action logs one line under its own activity, so the queries it starts share its trace id.
/// </summary>
public sealed partial class RetainersViewModel : GridQueryViewModel<DominatorRowItem>
{
    /// <summary>Children listed per node before a "more" row.</summary>
    public const int MaxChildren = 100;

    private readonly SnapshotSession _session;
    private readonly INavigator? _navigator;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private CancellationTokenSource? _icicle;
    private bool _queried;
    private bool _syncingSelection;
    private bool _navigating;

    // The row to keep the user's place on: asked with the query, found in its answer, selected once its rows are bound.
    private string? _revealKey;
    private string? _revealAsked;
    private int _revealIndex = -1;
    private string? _missingNote;

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

    /// <summary>The focus's dominators from the top, a long trail shortened in its middle.</summary>
    [ObservableProperty]
    private IReadOnlyList<TrailItem> _trail = [];

    /// <summary>How the dominators were obtained, what a row's bytes mean, and whether the rows were cut.</summary>
    [ObservableProperty]
    private string? _note;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FocusCommand), nameof(OpenObjectCommand), nameof(OpenInstancesCommand), nameof(OpenPathsCommand), nameof(RetainedBySetCommand))]
    private DominatorRowItem? _selectedRow;

    [ObservableProperty]
    private IReadOnlyList<IcicleSegmentDto> _icicleSegments = [];

    [ObservableProperty]
    private long _icicleTotal;

    [ObservableProperty]
    private IcicleSegmentDto? _selectedSegment;

    [ObservableProperty]
    private string? _icicleStatus;

    public RetainersViewModel(SnapshotSession session, IUiDispatcher dispatcher, ILogger<RetainersViewModel>? logger, INavigator? navigator, ILogger<RetainedBySetViewModel>? setLogger = null)
        : base(DominatorTreeColumns.Retained, dispatcher, logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _navigator = navigator;
        Set = new RetainedBySetViewModel(session, dispatcher, setLogger, navigator);
        OpenRowCommand = ToggleCommand;
    }

    public static IReadOnlyList<DominatorGroupingDto> Groupings { get; } = Enum.GetValues<DominatorGroupingDto>();

    /// <summary>The name of the set action, for the toolbar.</summary>
    public static string SetActionText => ValueBoundTexts.SetAction;

    public bool HasFocus => FocusAddress != 0;

    /// <summary>"Objects retained by this set" for the type last asked for.</summary>
    public RetainedBySetViewModel Set { get; }

    /// <summary>The icicle query in progress (tests and smoke runs await it).</summary>
    public Task IcicleTask { get; private set; } = Task.CompletedTask;

    /// <summary>The selection of the row a query revealed (tests and smoke runs await it).</summary>
    public Task SelectTask { get; private set; } = Task.CompletedTask;

    /// <summary>The expanded rows' keys (tests).</summary>
    public IReadOnlyCollection<string> Expanded => _expanded;

    protected override bool CanQuery => Snapshot is not null && IsActive;

    protected override string BusyText => "Listing the biggest retainers (the first time for a snapshot computes its dominators)...";

    protected override string EmptyText => "No root reaches anything in this snapshot.";

    private string? FocusKey => HasFocus ? DominatorTreeKeys.Object(FocusAddress) : null;

    /// <summary>Shows <paramref name="snapshot"/>'s biggest retainers (another snapshot was selected); queried when the tab is shown.</summary>
    public void Show(SnapshotHandle? snapshot)
    {
        ClearResults();
        Set.Show(snapshot, null, null);
        _expanded.Clear();
        _queried = false;
        Snapshot = snapshot;
        FocusAddress = 0;
        Trail = [];
        Start();
    }

    /// <summary>Follows a link to this tab: a focus object, a type's group, or a type's set.</summary>
    public void Navigate(NavigationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var activity = Act("link to {0}", target.Kind);
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
            return;
        }

        if (target.NodeKey is { } groupKey && target.TypeName is { } typeName)
        {
            // A type's group at the top level: group by type and open it. When none of the type's objects is itself a
            // top-level retainer there is no such group, and the answer says so (the 2.4 review).
            _navigating = true;
            Grouping = DominatorGroupingDto.Type;
            _navigating = false;
            FocusAddress = 0;
            _expanded.Clear();
            _expanded.Add(groupKey);
            _revealKey = groupKey;
            _missingNote = string.Create(
                CultureInfo.CurrentCulture,
                $"No {typeName} object is itself a top-level retainer: other objects hold them all. \"{ValueBoundTexts.SetAction}\" shows what they keep alive together; Show in dominator tree on one of its objects shows where it sits.");
            _queried = false;
        }

        Start();
    }

    partial void OnIsActiveChanged(bool value) => Start();

    partial void OnGroupingChanged(DominatorGroupingDto value)
    {
        if (_navigating)
        {
            return;
        }

        // Group keys of another grouping mean nothing now (the 2.4 review).
        using var activity = Act("group by {0}", value);
        _expanded.RemoveWhere(k => k.Contains("|g:", StringComparison.Ordinal) || k.Contains("|m:", StringComparison.Ordinal));
        RequeryTree();
    }

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

        // A bar selects its row when the row is in the loaded part of the tree; otherwise no row is selected, so the row
        // commands never act on another one (the 2.4 review). A double-click on the bar focuses on it.
        _syncingSelection = true;
        SelectedRow = FindLoadedRow(value.Key);
        _syncingSelection = false;
    }

    protected override async Task<GridQuery<DominatorRowItem>> RunQueryAsync(CancellationToken cancellationToken)
    {
        var finished = false;
        var progress = new Progress<JobProgress>(p => Status = finished || cancellationToken.IsCancellationRequested ? Status : ReferrersText.Progress(p));
        var reveal = _revealKey ?? FocusKey;
        var missing = _missingNote;
        _revealKey = null;
        _missingNote = null;
        var answer = await _session.QueryDominatorTreeAsync(Snapshot!, [.. _expanded], Grouping, FocusAddress, MaxChildren, reveal, progress, cancellationToken);
        finished = true;
        var result = answer.Result;
        var typeGroups = Grouping == DominatorGroupingDto.Type;
        return new GridQuery<DominatorRowItem>(
            answer.Rows,
            () =>
            {
                Trail = TrailOf(result);
                Totals = HasFocus
                    ? string.Create(CultureInfo.CurrentCulture, $"Retains {Format.Bytes(result.FocusRetained)} of {Format.Bytes(result.TotalRetained)} reachable")
                    : string.Create(CultureInfo.CurrentCulture, $"{result.TopLevel:N0} top-level retainers of {Format.Bytes(result.TotalRetained)} reachable");
                var bound = result.Rows.Columns.FirstOrDefault(c => c.Name == DominatorTreeColumns.Retained)?.Bound ?? ValueBound.Exact;
                Note = RetainedText.Source(result.Dominators) + " " + ValueBoundTexts.For(bound)
                    + (result.Truncated
                        ? string.Create(CultureInfo.CurrentCulture, $" The tree is cut at {DominatorTreeLimits.MaxRows:N0} rows; collapse rows or focus on one to see the rest.")
                        : string.Empty);
                (_revealAsked, _revealIndex) = (reveal, result.RevealIndex);
                _missingNote = result.RevealIndex < 0 ? missing : null;
            },
            onError => DominatorRowItem.CreateList(answer.Rows, result.Rows, typeGroups, onError, Logger));
    }

    protected override void OnRowsApplied()
    {
        var (key, index, missing) = (_revealAsked, _revealIndex, _missingNote);
        (_revealAsked, _revealIndex, _missingNote) = (null, -1, null);
        if (key is null)
        {
            return;
        }

        if (index >= 0)
        {
            SelectTask = SelectAsync(Rows, index);
        }
        else if (missing is not null)
        {
            _expanded.Remove(key);
            Status = missing;
            Logger.LogInformation("Retainers: {Key} is not in the tree as asked: {Note}", key, missing);
        }
    }

    /// <summary>Expands a collapsed row, or collapses an expanded one (a click on its arrow, Enter, a double-click).</summary>
    [RelayCommand]
    private void Toggle(DominatorRowItem? row)
    {
        // The open keys decide, not the row, whose state may be a query behind (the 2.4 review).
        if (row is { CanExpand: true } && row.Key != FocusKey)
        {
            SetExpanded(row, !_expanded.Contains(row.Key));
        }
    }

    [RelayCommand]
    private void Expand(DominatorRowItem? row)
    {
        if (row is { CanExpand: true } && row.Key != FocusKey && !_expanded.Contains(row.Key))
        {
            SetExpanded(row, true);
        }
    }

    [RelayCommand]
    private void Collapse(DominatorRowItem? row)
    {
        if (row is not null && _expanded.Contains(row.Key))
        {
            SetExpanded(row, false);
        }
    }

    /// <summary>Shows the tree below an object row, with its dominators as a trail.</summary>
    [RelayCommand(CanExecute = nameof(IsObjectRow))]
    private void Focus(DominatorRowItem? row)
    {
        if (row is { Address: not 0 })
        {
            using var activity = Act("focus on 0x{0:x}", row.Address);
            FocusOn(row.Address);
        }
    }

    [RelayCommand]
    private void FocusSegment(IcicleSegmentDto? segment)
    {
        if (segment is not null)
        {
            using var activity = Act("focus on the icicle's 0x{0:x}", segment.Address);
            FocusOn(segment.Address);
        }
    }

    [RelayCommand]
    private void FocusAncestor(TrailItem? item)
    {
        if (item?.Ancestor is { } ancestor)
        {
            using var activity = Act("focus on the trail's 0x{0:x}", ancestor.Address);
            FocusOn(ancestor.Address);
        }
    }

    /// <summary>Back to the biggest retainers.</summary>
    [RelayCommand]
    private void ShowTop()
    {
        using var activity = Act("show the biggest retainers", string.Empty);
        FocusOn(0);
    }

    [RelayCommand(CanExecute = nameof(IsObjectRow))]
    private void OpenObject(DominatorRowItem? row)
    {
        if (row is { Address: not 0 } && Snapshot is { } snapshot)
        {
            _navigator?.Navigate(NavigationTarget.Object(snapshot, row.Address));
        }
    }

    [RelayCommand(CanExecute = nameof(HasType))]
    private void OpenInstances(DominatorRowItem? row)
    {
        if (row?.TypeName is { } typeName && Snapshot is { } snapshot)
        {
            _navigator?.Navigate(NavigationTarget.Instances(snapshot, typeName, row.Module));
        }
    }

    [RelayCommand(CanExecute = nameof(IsObjectRow))]
    private void OpenPaths(DominatorRowItem? row)
    {
        if (row is { Address: not 0 } && Snapshot is { } snapshot)
        {
            _navigator?.Navigate(NavigationTarget.Object(snapshot, row.Address, ObjectReferenceTabs.Retention));
        }
    }

    [RelayCommand(CanExecute = nameof(HasType))]
    private void RetainedBySet(DominatorRowItem? row)
    {
        if (row?.TypeName is { } typeName && Snapshot is { } snapshot)
        {
            Set.Show(snapshot, typeName, row.Module);
            Set.Start();
        }
    }

    private static bool IsObjectRow(DominatorRowItem? row) => row is { Address: not 0 };

    private static bool HasType(DominatorRowItem? row) => row?.TypeName is not null;

    private static IReadOnlyList<TrailItem> TrailOf(DominatorTreeResult result)
    {
        var items = result.Ancestors.Select(a => new TrailItem(a.Name, a)).ToList();
        var omitted = result.AncestorDepth - result.Ancestors.Count;
        if (omitted > 0)
        {
            items.Insert(Math.Min(DominatorTreeLimits.TrailHead, items.Count), new TrailItem(string.Create(CultureInfo.CurrentCulture, $"{omitted:N0} more"), null));
        }

        return items;
    }

    /// <summary>Starts a user action: its log line, under an activity its queries join (the 2.4 review).</summary>
    private Activity? Act(string action, object argument)
    {
        var activity = new Activity("HeapScope.Retainers").Start();
        Logger.LogInformation("Retainers: {Action}", string.Format(CultureInfo.InvariantCulture, action, argument));
        return activity;
    }

    private void FocusOn(ulong address)
    {
        FocusAddress = address;
        _expanded.Clear();
        _queried = false;
        Start();
    }

    private void SetExpanded(DominatorRowItem row, bool expanded)
    {
        using var activity = Act(expanded ? "expand {0}" : "collapse {0}", row.Key);
        if (expanded)
        {
            _expanded.Add(row.Key);
        }
        else
        {
            _expanded.Remove(row.Key);
        }

        _revealKey = row.Key;
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
        // Its trace id joins the action's (2.3.7 review M1; the 2.4 review).
        using var activity = new Activity("HeapScope.Query").SetTag("grid", "Icicle").Start();
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
            // The grid shows the same refusal; the chart says it is empty rather than repeating it.
            IcicleSegments = [];
            IcicleStatus = null;
            Logger.LogInformation("Retainers: the icicle was refused: {Reason}", ex.Message);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The icicle chart failed");
            IcicleStatus = "The chart failed. Details are in the log.";
        }
    }

    private async Task SelectAsync(VirtualRowList<DominatorRowItem> rows, int index)
    {
        var row = rows.GetRow(index);
        try
        {
            await rows.WhenLoadedAsync();
        }
        catch (Exception ex) when (ex is WorkerException or OperationCanceledException)
        {
            return;
        }

        // Only while these rows are still the grid's (a newer query may have replaced them).
        if (ReferenceEquals(Rows, rows) && row.IsLoaded)
        {
            SelectedRow = row;
        }
    }

    private DominatorRowItem? FindLoadedRow(string key)
    {
        foreach (var page in Rows.RequestedPages)
        {
            for (var index = page * VirtualRowList.PageSize; index < Math.Min(Rows.Count, (page + 1) * VirtualRowList.PageSize); index++)
            {
                if (Rows.GetRow(index) is { IsLoaded: true } row && row.Key == key)
                {
                    return row;
                }
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
        SelectedRow = null;
        IcicleStatus = null;
        Note = null;
        (_revealKey, _revealAsked, _revealIndex, _missingNote) = (null, null, -1, null);
    }
}
