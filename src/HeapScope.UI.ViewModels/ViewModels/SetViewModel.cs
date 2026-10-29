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

    /// <summary>The members' key retention paths, as a list or a diagram (sprint 3.2).</summary>
    Paths = 4,

    /// <summary>The members grouped by how they are retained (sprint 3.2).</summary>
    SimilarRetention = 5,
}

/// <summary>
/// An object set as a subject of the Snapshot tab (sprint 3.1, ADR-030; dotMemory's object set): its Types, Instances,
/// Generations and Dominators views, and since 3.2 its Paths and Similar Retention views, each queried the first time it is
/// shown, and "objects retained by this set" on the
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
    private bool _closed;

    /// <summary>The view shown; switching to one queries it the first time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTypesShown), nameof(IsInstancesShown), nameof(IsGenerationsShown), nameof(IsDominatorsShown), nameof(IsPathsShown), nameof(IsSimilarRetentionShown))]
    private SetViews _view;

    [ObservableProperty]
    private InstancesViewModel? _instances;

    [ObservableProperty]
    private SetDominatorsViewModel? _dominators;

    [ObservableProperty]
    private TypePathsViewModel? _paths;

    [ObservableProperty]
    private SimilarRetentionViewModel? _similarRetention;

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

    public bool IsPathsShown => View == SetViews.Paths;

    public bool IsSimilarRetentionShown => View == SetViews.SimilarRetention;

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

        // The view's query starts inside this activity, so the worker's lines for it share its trace id (the 3.1 review).
        using var activity = Act("opened");
        Types.Show(Snapshot);
    }

    public void Close()
    {
        _closed = true;
        Types.Close();
        Instances?.Close();
        Dominators?.Close();
        Paths?.Close();
        SimilarRetention?.Close();
    }

    partial void OnViewChanged(SetViews value)
    {
        if (_closed)
        {
            // A step closed by the path keeps no new views (and their result sets) alive (the 3.1 review).
            return;
        }

        using var activity = Act("view " + value);
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
            case SetViews.Paths when Paths is null:
                Paths = new TypePathsViewModel(_session, Snapshot, Set, Title, _loggers?.CreateLogger<TypePathsViewModel>(), _navigator);
                Paths.Start();
                break;
            case SetViews.SimilarRetention when SimilarRetention is null:
                SimilarRetention = new SimilarRetentionViewModel(_session, Snapshot, Set, Title, _loggers?.CreateLogger<SimilarRetentionViewModel>(), _navigator);
                SimilarRetention.Start();
                break;
        }
    }

    private System.Diagnostics.Activity? Act(string action)
    {
        var activity = new System.Diagnostics.Activity("HeapScope.Sets").Start();
        _logger?.LogInformation("Set {Action} in snapshot {Snapshot}: {Shape}", action, Snapshot.Descriptor?.SnapshotId, Set.Spec.Shape);
        return activity;
    }
}
