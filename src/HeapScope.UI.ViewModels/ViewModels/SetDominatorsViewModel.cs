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

    /// <summary>The columns with their index in the cells.</summary>
    [ObservableProperty]
    private IReadOnlyList<(ResultColumn Column, int Index)> _columns = [];

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
                Columns = [.. result.Rows.Columns.Select((c, i) => (c, i)).Where(c => !c.c.IsHidden)];
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
