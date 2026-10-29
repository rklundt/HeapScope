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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.UI.ViewModels;

/// <summary>One Similar Retention group in the list: its totals and its (at most two) most different paths.</summary>
public sealed class RetentionGroupItem(RetentionGroupDto group, bool showFramework) : IObjectLink
{
    public RetentionGroupDto Group { get; } = group;

    /// <summary>The group's number as shown, from 1 (as the CLI and the logs number it).</summary>
    public int Number => Group.Number;

    public long Objects => Group.Objects;

    public long Bytes => Group.Bytes;

    public int PathCount => Group.PathCount;

    /// <summary>The first member's type, and how many types the group has besides.</summary>
    public string TypeText => Group.Types > 1 ? string.Create(CultureInfo.CurrentCulture, $"{Group.FirstType} and {Group.Types - 1:N0} more types") : Group.FirstType;

    public IReadOnlyList<KeyPathItem> Paths { get; } = [.. group.Paths.Select(p => new KeyPathItem(p, showFramework))];

    /// <summary>The paths as one text: root, then the path from it; one line per path.</summary>
    public string PathsText => string.Join(Environment.NewLine, Paths.Select(p => p.Root + ": " + p.PathText));

    public ulong LinkAddress => Group.ExampleAddress;
}

/// <summary>
/// Similar Retention of a set (sprint 3.2, ADR-031; dotMemory's Similar Retention): the members grouped by how they are
/// retained, each group with its most different paths; a group opens as a set of its own, and its paths as a diagram.
/// </summary>
public sealed partial class SimilarRetentionViewModel : ObservableObject
{
    private readonly SnapshotSession _session;
    private readonly ILogger _logger;
    private readonly INavigator? _navigator;
    private readonly CancellationTokenSource _lifetime = new();
    private SimilarRetentionResult? _result;

    [ObservableProperty]
    private IReadOnlyList<RetentionGroupItem> _groups = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Diagram))]
    private RetentionGroupItem? _selected;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _totals;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Diagram))]
    private bool _showFramework;

    public SimilarRetentionViewModel(SnapshotSession session, SnapshotHandle snapshot, ObjectSetQuery set, string title, ILogger? logger = null, INavigator? navigator = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Set = set ?? throw new ArgumentNullException(nameof(set));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        _logger = logger ?? NullLogger.Instance;
        _navigator = navigator;
        OpenGroupCommand = new RelayCommand<RetentionGroupItem?>(group =>
        {
            if (group is not null)
            {
                _navigator?.Navigate(NavigationTarget.ObjectSet(Snapshot, GroupSet(group), GroupTitle(group)));
            }
        });
        OpenExampleCommand = new RelayCommand<RetentionGroupItem?>(group =>
        {
            if (group is { LinkAddress: not 0 })
            {
                _navigator?.Navigate(NavigationTarget.Object(Snapshot, group.LinkAddress));
            }
        });
    }

    public SnapshotHandle Snapshot { get; }

    public ObjectSetQuery Set { get; }

    public string Title { get; }

    /// <summary>The selected group's paths as a diagram (the first group's when none is selected).</summary>
    public KeyPathDiagram Diagram => (Selected ?? Groups.FirstOrDefault()) is { } group
        ? KeyPathDiagram.Build(group.Group.Paths, ShowFramework, overlapping: true, target: string.Create(CultureInfo.CurrentCulture, $"Group {group.Number}"))
        : KeyPathDiagram.Empty;

    /// <summary>Opens a group as a set of its own (Enter or double click).</summary>
    public System.Windows.Input.ICommand OpenGroupCommand { get; }

    /// <summary>Opens a group's first member.</summary>
    public System.Windows.Input.ICommand OpenExampleCommand { get; }

    /// <summary>The load in progress (tests and smoke runs await it).</summary>
    public Task LoadTask { get; private set; } = Task.CompletedTask;

    public void Start() => LoadTask = LoadAsync(_lifetime.Token);

    public void Close() => _lifetime.Cancel();

    /// <summary>The set a group opens: the group of this set at its rank.</summary>
    public ObjectSetQuery GroupSet(RetentionGroupItem group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return Set with { Spec = ObjectSetSpec.RetentionGroupOf(Set.Spec, group.Group) };
    }

    private string GroupTitle(RetentionGroupItem group) => string.Create(CultureInfo.CurrentCulture, $"Group {group.Number} of {Title}");

    partial void OnShowFrameworkChanged(bool value) => ShowGroups();

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Status = "Grouping the objects by how they are retained (the first query of a snapshot finds every object's path and referrers)...";
        var finished = false;
        var progress = new Progress<JobProgress>(p =>
        {
            if (!finished && !cancellationToken.IsCancellationRequested)
            {
                Status = ReferrersText.Progress(p);
            }
        });
        try
        {
            var result = await _session.QuerySimilarRetentionAsync(Snapshot, Set, SimilarRetentionLimits.DefaultGroups, progress, cancellationToken);
            finished = true;
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _result = result;
            ShowGroups();
            Totals = string.Create(
                CultureInfo.CurrentCulture,
                $"{result.Set.Objects:N0} objects in {result.GroupCount:N0} groups by how they are retained; {result.Unreachable:N0} not reachable from any root ({Format.Bytes(result.UnreachableBytes)})")
                + (result.Unreachable > 0 ? ". " + result.UnreachableNote : string.Empty)
                + (result.GroupCount > result.Groups.Count ? string.Create(CultureInfo.CurrentCulture, $"; {result.GroupCount - result.Groups.Count:N0} smaller groups not listed hold {result.OtherObjects:N0} objects") : string.Empty)
                + (result.LimitedMembers > 0 ? string.Create(CultureInfo.CurrentCulture, $"; {result.LimitedMembers:N0} objects have more than {SimilarRetentionLimits.MaxReferrers} referrers and are grouped by the first {SimilarRetentionLimits.MaxReferrers}") : string.Empty)
                + ReferrersText.Incomplete(result.CompleteGraph).Replace("referrers", "paths", StringComparison.Ordinal);
            Status = result.Groups.Count == 0 ? "No object of the set is reachable from a root." : null;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Left the history while loading.
        }
        catch (Exception ex) when (ex is WorkerException or OperationCanceledException)
        {
            finished = true;
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            finished = true;
            _logger.LogError(ex, "Similar retention failed");
            Status = "The objects could not be grouped. Details are in the log.";
        }
    }

    private void ShowGroups()
    {
        var rank = Selected?.Group.Rank;
        Groups = _result is null ? [] : [.. _result.Groups.Select(g => new RetentionGroupItem(g, ShowFramework))];
        Selected = Groups.FirstOrDefault(g => g.Group.Rank == rank);
        OnPropertyChanged(nameof(Diagram));
    }
}
