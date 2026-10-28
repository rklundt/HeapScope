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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;


/// <summary>
/// The Snapshot tab's subjects and the analysis path (sprint 1.5, dotMemory's drill-down; a branching path per snapshot
/// since 2.7): the types grid is the root; links open instances, objects, a type's referrers (sprint 2.1) or its retention
/// paths (sprint 2.2) as new steps through <see cref="Navigate"/>, and links into the Retainers tab are recorded as steps
/// (<see cref="Record"/>). Each open snapshot keeps its own <see cref="AnalysisPath"/> while it is open, so switching
/// snapshots, tabs or pages never loses it. Back and Forward move along the path; choosing a step in the sidebar shows it;
/// <see cref="StepShown"/> tells the page which tab a step needs. The subjects follow their links through the
/// <see cref="INavigator"/> they were built with (2.3.7 review M8).
/// </summary>
public sealed partial class NavigatorViewModel : ObservableObject
{
    /// <summary>Steps kept per snapshot, the types grid included.</summary>
    public const int MaxHistory = AnalysisPath.MaxSteps;

    private readonly SnapshotSession _session;
    private readonly INavigator? _navigator;
    private readonly ILoggerFactory? _loggers;
    private readonly ILogger _logger;
    private readonly Dictionary<SnapshotHandle, AnalysisPath> _paths = [];
    private readonly AnalysisPath _noSnapshot;
    private AnalysisPath _path;
    private SnapshotHandle? _snapshot;
    private IReadOnlyList<AnalysisStep>? _steps;

    [ObservableProperty]
    private INavigationSubject _current;

    /// <summary>The current step; the sidebar selects it, and choosing another step shows that one.</summary>
    [ObservableProperty]
    private AnalysisStep? _currentStep;

    /// <param name="navigator">What the subjects' links go through: the Snapshots page, which picks the tab first.</param>
    public NavigatorViewModel(SnapshotSession session, TypeSummaryViewModel types, INavigator? navigator = null, ILoggerFactory? loggers = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Types = types ?? throw new ArgumentNullException(nameof(types));
        _navigator = navigator;
        _loggers = loggers;
        _logger = (ILogger?)loggers?.CreateLogger<NavigatorViewModel>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _noSnapshot = new AnalysisPath(types);
        _path = _noSnapshot;
        _current = types;
        _currentStep = _path.Current;
    }

    /// <summary>A step was shown by Back, Forward or the sidebar (not by a link, which picks its own tab).</summary>
    public event EventHandler<AnalysisStep>? StepShown;

    /// <summary>The root subject: the types grid of the selected snapshot.</summary>
    public TypeSummaryViewModel Types { get; }

    /// <summary>The selected snapshot's analysis path.</summary>
    public AnalysisPath Path => _path;

    // The view keeps one grid per kind of subject, built at window load, and only swaps their data: building a DataGrid
    // on every navigation took 136 to 332 ms on the dispatcher (sprint 1.5 smoke runs).

    /// <summary>Whether the types grid is the current subject.</summary>
    public bool IsTypesShown => Current is TypeSummaryViewModel;

    /// <summary>Whether an instances grid is the current subject.</summary>
    public bool IsInstancesShown => Current is InstancesViewModel;

    /// <summary>Whether an object is the current subject.</summary>
    public bool IsObjectShown => Current is ObjectViewModel;

    /// <summary>Whether a type's referrers are the current subject (sprint 2.1).</summary>
    public bool IsTypeReferrersShown => Current is TypeReferrersViewModel;

    /// <summary>Whether a type's retention paths are the current subject (sprint 2.2).</summary>
    public bool IsTypePathsShown => Current is TypePathsViewModel;

    /// <summary>Whether an object set is the current subject (sprint 3.1).</summary>
    public bool IsSetShown => Current is SetViewModel;

    /// <summary>The set shown last (the set view's data; kept while another subject is shown).</summary>
    [ObservableProperty]
    private SetViewModel? _shownSet;

    /// <summary>The type paths shown last (the view's data; kept while another subject is shown).</summary>
    [ObservableProperty]
    private TypePathsViewModel? _shownTypePaths;

    /// <summary>The type referrers shown last (the view's data; kept while another subject is shown).</summary>
    [ObservableProperty]
    private TypeReferrersViewModel? _shownTypeReferrers;

    /// <summary>The instances shown last (the instances view's data; kept while another subject is shown).</summary>
    [ObservableProperty]
    private InstancesViewModel? _shownInstances;

    /// <summary>The object shown last (the object view's data; kept while another subject is shown).</summary>
    [ObservableProperty]
    private ObjectViewModel? _shownObject;

    /// <summary>The steps from the root to the current one, for the breadcrumb.</summary>
    public IReadOnlyList<AnalysisStep> Trail => _path.Trail;

    /// <summary>Every step of the path in tree order, for the sidebar; the same list until the path's steps change.</summary>
    public IReadOnlyList<AnalysisStep> Steps => _steps ??= _path.Steps;

    public bool CanGoBack => _path.CanGoBack;

    public bool CanGoForward => _path.CanGoForward;

    /// <summary>
    /// Shows the path of <paramref name="snapshot"/> (another snapshot was selected), made with the types grid as its root
    /// the first time; null shows an empty path.
    /// </summary>
    public void Show(SnapshotHandle? snapshot)
    {
        if (snapshot is null)
        {
            _path = _noSnapshot;
        }
        else if (!_paths.TryGetValue(snapshot, out var path))
        {
            _paths[snapshot] = _path = new AnalysisPath(Types);
        }
        else
        {
            _path = path;
        }

        _snapshot = snapshot;
        _logger.LogInformation("Analysis path: showing snapshot {Snapshot} ({Steps} steps)", SnapshotId, _path.Count);
        ClearShown();
        Move();
    }

    /// <summary>Closes the path of a snapshot that was closed, releasing its subjects' result sets.</summary>
    public void Forget(SnapshotHandle snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_paths.Remove(snapshot, out var path))
        {
            path.CloseAll();
            if (path == _path)
            {
                Show(null);
            }
        }
    }

    /// <summary>Opens a target of the Snapshot tab as a new step; other targets are not this tab's and are ignored.</summary>
    public INavigationSubject? Navigate(NavigationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Kind switch
        {
            NavigationKind.Object => OpenObject(target.Snapshot, target.Address, target.ObjectTab),
            NavigationKind.Set when target.Set is { } set => OpenSet(target.Snapshot, set, target.SetTitle ?? "Set"),
            NavigationKind.Instances when target.Set is { } set => OpenSetInstances(target.Snapshot, set, target.SetTitle ?? "the set", target.TypeName ?? string.Empty, target.Module),
            NavigationKind.Instances when target.TypeName is { } typeName => OpenInstances(target.Snapshot, typeName, target.Module, target.Scope, target.SameValueAs),
            NavigationKind.TypeReferrers when target.TypeName is { } typeName => OpenTypeReferrers(target.Snapshot, typeName, target.Module),
            NavigationKind.TypePaths when target.TypeName is { } typeName => OpenTypePaths(target.Snapshot, typeName, target.Module),
            _ => null,
        };
    }

    /// <summary>Records a link into the Retainers tab as a step, replayed when it is chosen again (sprint 2.7).</summary>
    public AnalysisStep Record(NavigationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var step = _path.Open(target);
        Log("Opened", step);
        Move();
        return step;
    }

    /// <summary>Shows the objects of a type in <paramref name="snapshot"/>, all of them or those in a scope (sprint 1.6).</summary>
    public InstancesViewModel OpenInstances(SnapshotHandle snapshot, string typeName, string? module, InstanceScope scope = InstanceScope.All, ulong sameValueAs = 0)
    {
        var instances = new InstancesViewModel(_session, snapshot, typeName, module, scope, sameValueAs, Types.UiDispatcher, _loggers?.CreateLogger<InstancesViewModel>(), _navigator)
        {
            ShowRetained = Types.ShowRetained,
        };
        Push(instances);
        instances.Start();
        return instances;
    }

    /// <summary>Shows an object set with its views (sprint 3.1).</summary>
    public SetViewModel OpenSet(SnapshotHandle snapshot, ObjectSetQuery set, string title)
    {
        var subject = new SetViewModel(_session, snapshot, set, title, Types.UiDispatcher, _loggers, _navigator);
        Push(subject);
        subject.Start();
        return subject;
    }

    /// <summary>Shows a set's members, all of them or those of one type, as instances (sprint 3.1).</summary>
    public InstancesViewModel OpenSetInstances(SnapshotHandle snapshot, ObjectSetQuery set, string setTitle, string typeName, string? module)
    {
        var instances = new InstancesViewModel(_session, snapshot, set, setTitle, typeName, module, Types.UiDispatcher, _loggers?.CreateLogger<InstancesViewModel>(), _navigator)
        {
            ShowRetained = Types.ShowRetained,
        };
        Push(instances);
        instances.Start();
        return instances;
    }

    /// <summary>Shows one object of <paramref name="snapshot"/>, with <paramref name="tab"/> shown in its references panel.</summary>
    public ObjectViewModel OpenObject(SnapshotHandle snapshot, ulong address, ObjectReferenceTabs tab = ObjectReferenceTabs.Outgoing)
    {
        var obj = new ObjectViewModel(_session, snapshot, address, _loggers?.CreateLogger<ObjectViewModel>(), _navigator);
        Push(obj);
        obj.Start();
        obj.ReferencesView = tab;
        return obj;
    }

    /// <summary>Shows who references the objects of a type in <paramref name="snapshot"/> (sprint 2.1).</summary>
    public TypeReferrersViewModel OpenTypeReferrers(SnapshotHandle snapshot, string typeName, string? module)
    {
        var referrers = new TypeReferrersViewModel(_session, snapshot, typeName, module, Types.UiDispatcher, _loggers?.CreateLogger<TypeReferrersViewModel>(), _navigator);
        Push(referrers);
        referrers.Start();
        return referrers;
    }

    /// <summary>Shows why the objects of a type in <paramref name="snapshot"/> are alive (sprint 2.2).</summary>
    public TypePathsViewModel OpenTypePaths(SnapshotHandle snapshot, string typeName, string? module)
    {
        var paths = new TypePathsViewModel(_session, snapshot, typeName, module, _loggers?.CreateLogger<TypePathsViewModel>(), _navigator);
        Push(paths);
        paths.Start();
        return paths;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => ShowStep(_path.Back());

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void Forward() => ShowStep(_path.Forward());

    /// <summary>Closes a step and the steps opened from it (the sidebar's close button); the root stays.</summary>
    [RelayCommand]
    private void CloseStep(AnalysisStep? step)
    {
        if (step?.Parent is null)
        {
            return;
        }

        var wasShown = _path.Trail.Contains(step);
        var kind = KindOf(step);
        var closed = _path.Close(step);
        _logger.LogInformation("Analysis path: closed {Kind} step {Step} and {Below} below it in snapshot {Snapshot} ({Steps} steps)", kind, step.Id, closed - 1, SnapshotId, _path.Count);
        if (wasShown)
        {
            ShowStep(_path.Current);
        }
        else
        {
            Move();
        }
    }

    // The sidebar chose a step (two-way binding): show it.
    partial void OnCurrentStepChanged(AnalysisStep? value)
    {
        if (value is not null && value != _path.Current && _path.Steps.Contains(value))
        {
            ShowStep(_path.Select(value));
        }
    }

    private void ShowStep(AnalysisStep step)
    {
        Log("Showed", step);
        Move();
        StepShown?.Invoke(this, step);
    }

    private void Push(INavigationSubject subject)
    {
        var step = _path.Open(subject);
        Log("Opened", step);
        Move();
    }

    // The step's id, its parent's and the snapshot's tie a step to the subject's own query lines that follow (review).
    private void Log(string action, AnalysisStep step) =>
        _logger.LogInformation(
            "Analysis path: {Action} {Kind} step {Step} (from step {Parent}, depth {Depth}) in snapshot {Snapshot} ({Steps} steps)",
            action, KindOf(step), step.Id, step.Parent?.Id, step.Depth, SnapshotId, _path.Count);

    private string SnapshotId => _snapshot?.Descriptor?.SnapshotId.ToString() ?? "none";

    // Kinds are allow-listed names, never a type name or an address (ARCHITECTURE 11).
    private static string KindOf(AnalysisStep step) => step.Subject switch
    {
        null => step.Target!.Kind.ToString(),
        TypeSummaryViewModel => "Types",
        InstancesViewModel => "Instances",
        ObjectViewModel => "Object",
        TypeReferrersViewModel => "TypeReferrers",
        TypePathsViewModel => "TypePaths",
        SetViewModel => "Set",
        _ => "Subject",
    };

    private void ClearShown()
    {
        ShownInstances = null;
        ShownObject = null;
        ShownTypeReferrers = null;
        ShownTypePaths = null;
        ShownSet = null;
    }

    private void Move()
    {
        CurrentStep = _path.Current;
        Current = _path.Current.SubjectShown;
        switch (Current)
        {
            case InstancesViewModel instances:
                ShownInstances = instances;
                break;
            case ObjectViewModel obj:
                ShownObject = obj;
                break;
            case TypeReferrersViewModel referrers:
                ShownTypeReferrers = referrers;
                break;
            case TypePathsViewModel paths:
                ShownTypePaths = paths;
                break;
            case SetViewModel set:
                ShownSet = set;
                break;
        }

        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(IsTypesShown));
        OnPropertyChanged(nameof(IsInstancesShown));
        OnPropertyChanged(nameof(IsObjectShown));
        OnPropertyChanged(nameof(IsTypeReferrersShown));
        OnPropertyChanged(nameof(IsTypePathsShown));
        OnPropertyChanged(nameof(IsSetShown));
        OnPropertyChanged(nameof(Trail));
        if (_steps is null || _steps.Count != _path.Count || !_steps.SequenceEqual(_path.Steps))
        {
            _steps = null;
            OnPropertyChanged(nameof(Steps));
        }

        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
    }
}
