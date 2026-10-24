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

namespace HeapScope.UI.Wpf.ViewModels;

/// <summary>
/// The Snapshot tab's subjects and history (sprint 1.5, dotMemory's drill-down): the types grid is the root; opening a
/// type row shows its instances, opening an instance, a reference, a referrer or a field link shows that object; a type's
/// instances open who references them (sprint 2.1), whose rows open the referrer type's instances. Back and Forward
/// move through the history; opening something after going back drops the forward entries (their result sets are
/// released). At most <see cref="MaxHistory"/> subjects are kept.
/// </summary>
public sealed partial class NavigatorViewModel : ObservableObject
{
    /// <summary>Subjects kept in the history, the types grid included.</summary>
    public const int MaxHistory = 30;

    private readonly SnapshotSession _session;
    private readonly ILoggerFactory? _loggers;
    private readonly List<object> _history = [];
    private int _position;

    [ObservableProperty]
    private object _current;

    public NavigatorViewModel(SnapshotSession session, TypeSummaryViewModel types, ILoggerFactory? loggers = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Types = types ?? throw new ArgumentNullException(nameof(types));
        _loggers = loggers;
        _history.Add(types);
        _current = types;
        Types.OpenRowCommand = OpenTypeRowCommand;
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
    public IReadOnlyList<object> Trail => _history.Take(_position + 1).ToList();

    public bool CanGoBack => _position > 0;

    public bool CanGoForward => _position < _history.Count - 1;

    /// <summary>Back to the types grid with an empty history (another snapshot was selected).</summary>
    public void Reset()
    {
        foreach (var subject in _history.Skip(1))
        {
            CloseSubject(subject);
        }

        _history.RemoveRange(1, _history.Count - 1);
        ShownInstances = null;
        ShownObject = null;
        ShownTypeReferrers = null;
        Move(0);
    }

    /// <summary>Shows the objects of a type in <paramref name="snapshot"/>, all of them or those in a scope (sprint 1.6).</summary>
    public InstancesViewModel OpenInstances(SnapshotHandle snapshot, string typeName, string? module, InstanceScope scope = InstanceScope.All, ulong sameValueAs = 0)
    {
        var instances = new InstancesViewModel(_session, snapshot, typeName, module, scope, sameValueAs, _loggers?.CreateLogger<InstancesViewModel>());
        instances.OpenRowCommand = OpenInstanceRowCommand;
        instances.ReferencedByCommand = OpenReferencedByCommand;
        Push(instances);
        instances.Start();
        return instances;
    }

    /// <summary>Shows one object of <paramref name="snapshot"/>.</summary>
    public ObjectViewModel OpenObject(SnapshotHandle snapshot, ulong address)
    {
        var obj = new ObjectViewModel(_session, snapshot, address, _loggers?.CreateLogger<ObjectViewModel>());
        obj.OpenCommand = OpenObjectLinkCommand;
        Push(obj);
        obj.Start();
        return obj;
    }

    /// <summary>Shows who references the objects of a type in <paramref name="snapshot"/> (sprint 2.1).</summary>
    public TypeReferrersViewModel OpenTypeReferrers(SnapshotHandle snapshot, string typeName, string? module)
    {
        var referrers = new TypeReferrersViewModel(_session, snapshot, typeName, module, _loggers?.CreateLogger<TypeReferrersViewModel>());
        referrers.OpenRowCommand = OpenTypeReferrerRowCommand;
        Push(referrers);
        referrers.Start();
        return referrers;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Move(_position - 1);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void Forward() => Move(_position + 1);

    [RelayCommand]
    private void OpenTypeRow(TypeRowItem? row)
    {
        if (row is not { IsLoaded: true } || Types.Snapshot is not { } snapshot)
        {
            return;
        }

        if (row.TypeName is null)
        {
            Types.Status = "Group by type to open a row's objects.";
            return;
        }

        OpenInstances(snapshot, row.TypeName, row.Module);
    }

    [RelayCommand]
    private void OpenInstanceRow(InstanceRowItem? row)
    {
        if (row is { IsLoaded: true, Address: not 0 } && Current is InstancesViewModel instances)
        {
            OpenObject(instances.Snapshot, row.Address);
        }
    }

    [RelayCommand]
    private void OpenReferencedBy()
    {
        if (Current is InstancesViewModel instances)
        {
            OpenTypeReferrers(instances.Snapshot, instances.TypeName, instances.Module);
        }
    }

    [RelayCommand]
    private void OpenTypeReferrerRow(TypeReferrerRowItem? row)
    {
        if (row is { IsLoaded: true } && Current is TypeReferrersViewModel referrers)
        {
            OpenInstances(referrers.Snapshot, row.TypeName, row.Module);
        }
    }

    [RelayCommand]
    private void OpenObjectLink(object? link)
    {
        var address = link switch
        {
            ReferenceRowItem { IsLoaded: true } reference => reference.Address,
            ReferrerRowItem { IsLoaded: true } referrer => referrer.Address,
            FieldNode { IsLink: true } field => field.Address,
            _ => 0UL,
        };

        if (address != 0 && Current is ObjectViewModel obj)
        {
            OpenObject(obj.Snapshot, address);
        }
    }

    private void Push(object subject)
    {
        // Opening after going back replaces the forward history.
        foreach (var dropped in _history.Skip(_position + 1))
        {
            CloseSubject(dropped);
        }

        _history.RemoveRange(_position + 1, _history.Count - _position - 1);
        _history.Add(subject);
        if (_history.Count > MaxHistory)
        {
            CloseSubject(_history[1]);
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
        }

        OnPropertyChanged(nameof(IsTypesShown));
        OnPropertyChanged(nameof(IsInstancesShown));
        OnPropertyChanged(nameof(IsObjectShown));
        OnPropertyChanged(nameof(IsTypeReferrersShown));
        OnPropertyChanged(nameof(Trail));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
    }

    private static void CloseSubject(object subject)
    {
        switch (subject)
        {
            case InstancesViewModel instances:
                instances.Close();
                break;
            case ObjectViewModel obj:
                obj.Close();
                break;
            case TypeReferrersViewModel referrers:
                referrers.Close();
                break;
        }
    }
}
