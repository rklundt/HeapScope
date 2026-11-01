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
using HeapScope.Application.Presentation;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;

/// <summary>Texts the referrer views share (sprint 2.1 review M7, M9).</summary>
internal static class ReferrersText
{
    /// <summary>A worker phase as a status line: "Indexing incoming references... 42%".</summary>
    public static string Progress(JobProgress progress) =>
        progress.Total is > 0 and var total
            ? string.Create(CultureInfo.CurrentCulture, $"{progress.Phase}... {100.0 * progress.Completed / total:N0}%")
            : progress.Phase + "...";

    /// <summary>The note for an incomplete snapshot (a sampled gcdump, a truncated dump), or nothing.</summary>
    public static string Incomplete(bool completeGraph) =>
        completeGraph ? string.Empty : " (this snapshot is incomplete, so some referrers may be missing)";
}

/// <summary>Texts about retained sizes (sprint 2.3).</summary>
internal static class RetainedText
{
    /// <summary>Why retained sizes are not shown after a failed retained query (review M8), with the worker's reason.</summary>
    public static string NotShown(string reason) => "Retained sizes are not shown: " + reason;

    /// <summary>What an object's retained bytes mean.</summary>
    public const string ObjectsRetain = "An object's retained bytes are what it alone keeps alive; objects no root reaches retain nothing (blank).";
}

/// <summary>One object referencing the inspected object (sprint 2.1), filled when its page arrives.</summary>
public sealed class ReferrerRowItem(int index) : ObservableObject, IIndexedRow, IObjectLink
{
    public int Index { get; } = index;

    public string Field { get; private set; } = string.Empty;

    public ulong Address { get; private set; }

    public ulong LinkAddress => IsLoaded ? Address : 0;

    public string AddressText { get; private set; } = string.Empty;

    public string Type { get; private set; } = string.Empty;

    public long Bytes { get; private set; }

    /// <summary>How many of the referrer's references point at the object.</summary>
    public long References { get; private set; }

    public bool IsLoaded { get; private set; }

    /// <summary>A row list over a referrers result set; fails at once when a column is missing.</summary>
    public static VirtualRowList<ReferrerRowItem> CreateList(ResultSetHandle? rows, ResultSetInfo info, Action<Exception>? onError = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        int[] layout =
        [
            ResultColumns.Require(info.Columns, ReferrerColumns.Field),
            ResultColumns.Require(info.Columns, ReferrerColumns.Address),
            ResultColumns.Require(info.Columns, ReferrerColumns.Type),
            ResultColumns.Require(info.Columns, ReferrerColumns.Bytes),
            ResultColumns.Require(info.Columns, ReferrerColumns.References),
        ];
        return new VirtualRowList<ReferrerRowItem>(rows, info.TotalCount, i => new ReferrerRowItem(i), (row, cells) => row.Fill(cells, layout), onError, logger);
    }

    /// <summary>An empty list (before the incoming references are loaded).</summary>
    public static VirtualRowList<ReferrerRowItem> Empty() => CreateList(null, new ResultSetInfo(Guid.Empty, 0,
    [
        new(ReferrerColumns.Field, ResultColumnKind.Text), new(ReferrerColumns.Address, ResultColumnKind.Address, true),
        new(ReferrerColumns.Type, ResultColumnKind.Text), new(ReferrerColumns.Bytes, ResultColumnKind.Bytes),
        new(ReferrerColumns.References, ResultColumnKind.Integer),
    ]));

    private void Fill(IReadOnlyList<string?> cells, int[] layout)
    {
        Field = Cells.Text(cells, layout[0]);
        AddressText = Cells.Text(cells, layout[1]);
        Address = Cells.Address(AddressText);
        Type = Cells.Text(cells, layout[2]);
        Bytes = Cells.Long(cells, layout[3]);
        References = Cells.Long(cells, layout[4]);
        IsLoaded = true;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>One referrer type of a type's objects (sprint 2.1), filled when its page arrives.</summary>
public sealed class TypeReferrerRowItem(int index) : ObservableObject, IIndexedRow
{
    public int Index { get; } = index;

    public string TypeName { get; private set; } = string.Empty;

    public string Module { get; private set; } = string.Empty;

    public long Objects { get; private set; }

    public long References { get; private set; }

    public long Targets { get; private set; }

    public long Bytes { get; private set; }

    public bool IsLoaded { get; private set; }

    /// <summary>A row list over a type referrers result set; fails at once when a column is missing.</summary>
    public static VirtualRowList<TypeReferrerRowItem> CreateList(ResultSetHandle? rows, ResultSetInfo info, Action<Exception>? onError = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        int[] layout =
        [
            ResultColumns.Require(info.Columns, TypeReferrerColumns.Type),
            ResultColumns.Require(info.Columns, TypeReferrerColumns.Module),
            ResultColumns.Require(info.Columns, TypeReferrerColumns.Objects),
            ResultColumns.Require(info.Columns, TypeReferrerColumns.References),
            ResultColumns.Require(info.Columns, TypeReferrerColumns.Targets),
            ResultColumns.Require(info.Columns, TypeReferrerColumns.Bytes),
        ];
        return new VirtualRowList<TypeReferrerRowItem>(rows, info.TotalCount, i => new TypeReferrerRowItem(i), (row, cells) => row.Fill(cells, layout), onError, logger);
    }

    private void Fill(IReadOnlyList<string?> cells, int[] layout)
    {
        TypeName = Cells.Text(cells, layout[0]);
        Module = Cells.Text(cells, layout[1]);
        Objects = Cells.Long(cells, layout[2]);
        References = Cells.Long(cells, layout[3]);
        Targets = Cells.Long(cells, layout[4]);
        Bytes = Cells.Long(cells, layout[5]);
        IsLoaded = true;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// Who references the objects of a type (sprint 2.1, dotMemory's incoming references for a set): one row per referrer
/// type, sortable by any column; opening a row shows that type's objects. Opened from a type's instances.
/// </summary>
public sealed partial class TypeReferrersViewModel : GridQueryViewModel<TypeReferrerRowItem>, INavigationSubject
{
    private readonly SnapshotSession _session;

    public TypeReferrersViewModel(SnapshotSession session, SnapshotHandle snapshot, string typeName, string? module, IUiDispatcher dispatcher, ILogger? logger = null, INavigator? navigator = null)
        : base(TypeReferrerColumns.References, dispatcher, logger)
    {
        OpenRowCommand = new RelayCommand<TypeReferrerRowItem?>(row =>
        {
            if (row is { IsLoaded: true })
            {
                navigator?.Navigate(NavigationTarget.Instances(snapshot, row.TypeName, row.Module));
            }
        });
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
        Module = module;
    }

    public SnapshotHandle Snapshot { get; }

    public string TypeName { get; }

    public string? Module { get; }

    /// <summary>The navigation title.</summary>
    public string Title => "Referrers of " + (string.IsNullOrEmpty(Module) ? TypeName : $"{TypeName} [{Module}]");

    protected override bool CanQuery => true;

    protected override string BusyText => "Finding referrers (the first query of a snapshot indexes its incoming references)...";

    protected override string EmptyText => "Nothing references objects of this type." + ReferrersText.Incomplete(_complete);

    private bool _complete = true;

    /// <summary>Runs the first query (after the navigator wires the drill-down command).</summary>
    public void Start() => Requery();

    protected override bool SortsDescendingFirst(string column) => column is not (TypeReferrerColumns.Type or TypeReferrerColumns.Module);

    protected override async Task<GridQuery<TypeReferrerRowItem>> RunQueryAsync(CancellationToken cancellationToken)
    {
        // The first query builds the snapshot's index in the worker: its phases replace the busy text (review M7).
        var finished = false;
        var progress = new Progress<JobProgress>(p =>
        {
            if (!finished && !cancellationToken.IsCancellationRequested)
            {
                Status = ReferrersText.Progress(p);
            }
        });
        TypeReferrerList list;
        try
        {
            list = await _session.QueryTypeReferrersAsync(Snapshot, TypeName, Module, SortColumn, Descending, progress, cancellationToken);
        }
        finally
        {
            finished = true;
        }

        var result = list.Result;
        return new GridQuery<TypeReferrerRowItem>(
            list.Rows,
            () =>
            {
                _complete = result.CompleteGraph;
                Totals = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{result.ReferenceCount:N0} references to {result.TargetObjects:N0} objects, from {result.Rows.TotalCount:N0} types") + ReferrersText.Incomplete(result.CompleteGraph);
            },
            onError => TypeReferrerRowItem.CreateList(list.Rows, result.Rows, onError, Logger));
    }
}
