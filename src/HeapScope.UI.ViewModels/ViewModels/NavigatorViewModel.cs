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
/// The Snapshot tab's subjects and history (sprint 1.5, dotMemory's drill-down): the types grid is the root; links open
/// instances, objects, a type's referrers (sprint 2.1) or its retention paths (sprint 2.2) as new subjects through
/// <see cref="Navigate"/>. Back and Forward move through the history; opening something after going back drops the
/// forward entries (their result sets are released). At most <see cref="MaxHistory"/> subjects are kept. Since sprint
/// 2.4 (2.3.7 review M8) the history is typed (<see cref="INavigationSubject"/>) and the subjects follow their links
/// through the <see cref="INavigator"/> they were built with.
/// </summary>
public sealed partial class NavigatorViewModel : ObservableObject
{
    /// <summary>Subjects kept in the history, the types grid included.</summary>
    public const int MaxHistory = 30;

    private readonly SnapshotSession _session;
    private readonly INavigator? _navigator;
    private readonly ILoggerFactory? _loggers;
    private readonly List<INavigationSubject> _history = [];
    private int _position;

    [ObservableProperty]
    private INavigationSubject _current;

    /// <param name="navigator">What the subjects' links go through: the Snapshots page, which picks the tab first.</param>
    public NavigatorViewModel(SnapshotSession session, TypeSummaryViewModel types, INavigator? navigator = null, ILoggerFactory? loggers = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Types = types ?? throw new ArgumentNullException(nameof(types));
        _navigator = navigator;
        _loggers = loggers;
        _history.Add(types);
        _current = types;
    }

    /// <summary>The root subject: the types grid of the selected snapshot.</summary>
    public TypeSummaryViewModel Types { get; }

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

    /// <summary>The subjects from the root to the current one, for the breadcrumb.</summary>
    public IReadOnlyList<INavigationSubject> Trail => _history.Take(_position + 1).ToList();

    public bool CanGoBack => _position > 0;

    public bool CanGoForward => _position < _history.Count - 1;

    /// <summary>Back to the types grid with an empty history (another snapshot was selected).</summary>
    public void Reset()
    {
        foreach (var subject in _history.Skip(1))
        {
            subject.Close();
        }

        _history.RemoveRange(1, _history.Count - 1);
        ShownInstances = null;
        ShownObject = null;
        ShownTypeReferrers = null;
        ShownTypePaths = null;
        Move(0);
    }

    /// <summary>Opens a target of the Snapshot tab as a new subject; other targets are not this tab's and are ignored.</summary>
    public INavigationSubject? Navigate(NavigationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Kind switch
        {
            NavigationKind.Object => OpenObject(target.Snapshot, target.Address, target.ObjectTab),
            NavigationKind.Instances when target.TypeName is { } typeName => OpenInstances(target.Snapshot, typeName, target.Module, target.Scope, target.SameValueAs),
            NavigationKind.TypeReferrers when target.TypeName is { } typeName => OpenTypeReferrers(target.Snapshot, typeName, target.Module),
            NavigationKind.TypePaths when target.TypeName is { } typeName => OpenTypePaths(target.Snapshot, typeName, target.Module),
            _ => null,
        };
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
    private void Back() => Move(_position - 1);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void Forward() => Move(_position + 1);

    private void Push(INavigationSubject subject)
    {
        // Opening after going back replaces the forward history.
        foreach (var dropped in _history.Skip(_position + 1))
        {
            dropped.Close();
        }

        _history.RemoveRange(_position + 1, _history.Count - _position - 1);
        _history.Add(subject);
        if (_history.Count > MaxHistory)
        {
            _history[1].Close();
            _history.RemoveAt(1);
        }

        Move(_history.Count - 1);
    }

    private void Move(int position)
    {
        _position = position;
        Current = _history[position];
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
        }

        OnPropertyChanged(nameof(IsTypesShown));
        OnPropertyChanged(nameof(IsInstancesShown));
        OnPropertyChanged(nameof(IsObjectShown));
        OnPropertyChanged(nameof(IsTypeReferrersShown));
        OnPropertyChanged(nameof(IsTypePathsShown));
        OnPropertyChanged(nameof(Trail));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
    }
}
