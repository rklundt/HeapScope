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
using HeapScope.Contracts;
using HeapScope.UI.Services;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;

/// <summary>The views of an object set (sprint 3.1), in the order the set's view switcher lists them.</summary>
public enum SetViews
{
    Types = 0,
    Instances = 1,
    Generations = 2,
    Dominators = 3,
}

/// <summary>
/// An object set as a subject of the Snapshot tab (sprint 3.1, ADR-030; dotMemory's object set): its Types, Instances,
/// Generations and Dominators views, each queried the first time it is shown, and "objects retained by this set" on the
/// Retainers tab. The set is kept as its spec, so the views re-run it after an eviction or a worker restart.
/// </summary>
public sealed partial class SetViewModel : ObservableObject, INavigationSubject
{
    private readonly SnapshotSession _session;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILoggerFactory? _loggers;
    private readonly ILogger? _logger;
    private readonly INavigator? _navigator;
    private bool _started;

    /// <summary>The view shown; switching to one queries it the first time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTypesShown), nameof(IsInstancesShown), nameof(IsGenerationsShown), nameof(IsDominatorsShown))]
    private SetViews _view;

    [ObservableProperty]
    private InstancesViewModel? _instances;

    [ObservableProperty]
    private SetDominatorsViewModel? _dominators;

    public SetViewModel(SnapshotSession session, SnapshotHandle snapshot, ObjectSetQuery set, string title, IUiDispatcher dispatcher, ILoggerFactory? loggers = null, INavigator? navigator = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Set = set ?? throw new ArgumentNullException(nameof(set));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _loggers = loggers;
        _logger = loggers?.CreateLogger<SetViewModel>();
        _navigator = navigator;
        Types = new TypeSummaryViewModel(session, dispatcher, set, title, loggers?.CreateLogger<TypeSummaryViewModel>(), navigator);
        RetainedCommand = new RelayCommand(() => navigator?.Navigate(NavigationTarget.RetainedBySet(Snapshot, Set, Title)));
    }

    public SnapshotHandle Snapshot { get; }

    SnapshotHandle? INavigationSubject.Snapshot => Snapshot;

    public ObjectSetQuery Set { get; }

    /// <summary>The set's name in the analysis path ("3 types", "Namespace App.Orders", "Bookmarks (12)").</summary>
    public string Title { get; }

    /// <summary>The set's members by type, namespace or assembly; its totals and heap kinds are the set's.</summary>
    public TypeSummaryViewModel Types { get; }

    public IReadOnlyList<SetViews> Views { get; } = Enum.GetValues<SetViews>();

    public bool IsTypesShown => View == SetViews.Types;

    public bool IsInstancesShown => View == SetViews.Instances;

    public bool IsGenerationsShown => View == SetViews.Generations;

    public bool IsDominatorsShown => View == SetViews.Dominators;

    /// <summary>Opens what the set's members retain together, exactly, on the Retainers tab.</summary>
    public IRelayCommand RetainedCommand { get; }

    /// <summary>Queries the Types view (its totals feed Generations); the navigator calls it once the step is open.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _logger?.LogInformation("Set opened: {Shape}", Set.Spec.Shape);
        Types.Show(Snapshot);
    }

    public void Close()
    {
        Types.Close();
        Instances?.Close();
        Dominators?.Close();
    }

    partial void OnViewChanged(SetViews value)
    {
        _logger?.LogInformation("Set view: {View} of {Shape}", value, Set.Spec.Shape);
        switch (value)
        {
            case SetViews.Instances when Instances is null:
                Instances = new InstancesViewModel(_session, Snapshot, Set, Title, string.Empty, null, _dispatcher, _loggers?.CreateLogger<InstancesViewModel>(), _navigator);
                Instances.Start();
                break;
            case SetViews.Dominators when Dominators is null:
                Dominators = new SetDominatorsViewModel(_session, Snapshot, Set, _dispatcher, _loggers?.CreateLogger<SetDominatorsViewModel>(), _navigator);
                Dominators.Start();
                break;
        }
    }
}

/// <summary>
/// A set's Dominators view (sprint 3.1): the members no other member dominates, most retained first, with the other
/// members below each. Their retained sizes never overlap, so they add up. A row opens its object.
/// </summary>
public sealed partial class SetDominatorsViewModel : GridQueryViewModel<CellRowItem>
{
    private readonly SnapshotSession _session;

    /// <summary>What the view shows and how the dominators were obtained.</summary>
    [ObservableProperty]
    private string? _summary;

    public SetDominatorsViewModel(SnapshotSession session, SnapshotHandle snapshot, ObjectSetQuery set, IUiDispatcher dispatcher, ILogger? logger = null, INavigator? navigator = null)
        : base(SetDominatorColumns.Retained, dispatcher, logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Set = set ?? throw new ArgumentNullException(nameof(set));
        OpenRowCommand = new RelayCommand<CellRowItem?>(row =>
        {
            if (row?[SetDominatorColumns.Address] is { } text && Cells.Address(text) is var address and not 0)
            {
                navigator?.Navigate(NavigationTarget.Object(Snapshot, address));
            }
        });
        ShowInRetainersCommand = new RelayCommand<CellRowItem?>(row =>
        {
            if (row?[SetDominatorColumns.Address] is { } text && Cells.Address(text) is var address and not 0)
            {
                navigator?.Navigate(NavigationTarget.Retainers(Snapshot, address));
            }
        });
    }

    public SnapshotHandle Snapshot { get; }

    public ObjectSetQuery Set { get; }

    /// <summary>Opens a row's object in the dominator tree, on the Retainers tab.</summary>
    public System.Windows.Input.ICommand ShowInRetainersCommand { get; }

    protected override bool CanQuery => true;

    protected override string BusyText => "Finding the set's dominators...";

    protected override string EmptyText => "No root reaches any member of the set.";

    public void Start() => Requery();

    protected override async Task<GridQuery<CellRowItem>> RunQueryAsync(CancellationToken cancellationToken)
    {
        var finished = false;
        var progress = new Progress<JobProgress>(p => Status = finished || cancellationToken.IsCancellationRequested ? Status : ReferrersText.Progress(p));
        var answer = await _session.QuerySetDominatorsAsync(Snapshot, Set, progress, cancellationToken);
        finished = true;
        var result = answer.Result;
        return new GridQuery<CellRowItem>(
            answer.Rows,
            () =>
            {
                Summary = Describe(result) + " " + RetainedText.Source(result.Dominators);
                Totals = string.Create(CultureInfo.CurrentCulture, $"{result.TopMembers:N0} rows");
            },
            onError => CellRowItem.CreateList(answer.Rows, result.Rows, onError, Logger));
    }

    /// <summary>What the rows are: the members no other member dominates, what they retain apart, and the rest of the set.</summary>
    public static string Describe(SetDominatorsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var below = result.Set.Objects - result.TopMembers - result.UnreachableMembers;
        return string.Create(
            CultureInfo.CurrentCulture,
            $"{result.TopMembers:N0} of the set's {result.Set.Objects:N0} objects are not held by another member; together they retain {Format.Bytes(result.TopRetained)}. {below:N0} more are below them{(result.UnreachableMembers > 0 ? $", and {result.UnreachableMembers:N0} no root reaches" : string.Empty)}.");
    }
}
