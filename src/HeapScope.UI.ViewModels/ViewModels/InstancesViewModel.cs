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

/// <summary>One object in the instances grid, filled when its page arrives.</summary>
public sealed class InstanceRowItem(int index) : ObservableObject, IIndexedRow, IObjectLink
{
    public int Index { get; } = index;

    public ulong Address { get; private set; }

    public string AddressText { get; private set; } = string.Empty;

    public string Type { get; private set; } = string.Empty;

    public long Bytes { get; private set; }

    public string Generation { get; private set; } = string.Empty;

    public string Value { get; private set; } = string.Empty;

    /// <summary>The object's retained bytes (sprint 2.3); null when not asked for or when no root reaches it.</summary>
    public long? Retained { get; private set; }

    public bool IsLoaded { get; private set; }

    public ulong LinkAddress => IsLoaded ? Address : 0;

    public string? LinkTypeName => IsLoaded ? Type : null;

    /// <summary>A row list over an instances result set; fails at once when a column is missing.</summary>
    public static VirtualRowList<InstanceRowItem> CreateList(ResultSetHandle? rows, ResultSetInfo info, Action<Exception>? onError = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        int[] layout =
        [
            ResultColumns.Require(info.Columns, InstanceColumns.Address),
            ResultColumns.Require(info.Columns, InstanceColumns.Type),
            ResultColumns.Require(info.Columns, InstanceColumns.Bytes),
            ResultColumns.Require(info.Columns, InstanceColumns.Generation),
            ResultColumns.Require(info.Columns, InstanceColumns.Value),
            ResultColumns.Find(info.Columns, InstanceColumns.Retained),
        ];
        return new VirtualRowList<InstanceRowItem>(rows, info.TotalCount, i => new InstanceRowItem(i), (row, cells) => row.Fill(cells, layout), onError, logger);
    }

    private void Fill(IReadOnlyList<string?> cells, int[] layout)
    {
        AddressText = Cells.Text(cells, layout[0]);
        Address = Cells.Address(AddressText);
        Type = Cells.Text(cells, layout[1]);
        Bytes = Cells.Long(cells, layout[2]);
        Generation = Cells.Text(cells, layout[3]);
        Value = Cells.Text(cells, layout[4]);
        Retained = layout[5] >= 0 && layout[5] < cells.Count && cells[layout[5]] is not null ? Cells.Long(cells, layout[5]) : null;
        IsLoaded = true;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// The objects of a type (sprint 1.5, dotMemory's instances view): sortable by any column, filtered by address prefix
/// (<c>0x...</c>), regular expression (<c>/.../</c>) or text in the value; opened from a type or compare row.
/// </summary>
public sealed partial class InstancesViewModel : GridQueryViewModel<InstanceRowItem>, INavigationSubject
{
    private readonly SnapshotSession _session;

    [ObservableProperty]
    private string? _valuesExplanation;

    /// <summary>Whether the user asked for retained bytes (sprint 2.3); the navigator copies the types grid's choice.</summary>
    [ObservableProperty]
    private bool _showRetained;

    /// <summary>Whether the shown rows carry retained bytes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleColumnGroups))]
    private bool _retainedColumnShown;

    /// <summary>The optional column groups shown (2.3.7 review M13).</summary>
    public IReadOnlyList<string> VisibleColumnGroups => ColumnGroups.Of((ColumnGroups.Retained, RetainedColumnShown));

    /// <summary>How the retained sizes were obtained and what they mean; null without them.</summary>
    [ObservableProperty]
    private string? _retainedNote;

    private bool _started;
    private bool _retainedOffQuietly;

    public InstancesViewModel(SnapshotSession session, SnapshotHandle snapshot, string typeName, string? module, IUiDispatcher dispatcher, ILogger? logger = null, INavigator? navigator = null)
        : this(session, snapshot, typeName, module, InstanceScope.All, 0, dispatcher, logger, navigator)
    {
    }

    /// <summary>The objects of a type within a scope (sprint 1.6: what an inspection row opens).</summary>
    public InstancesViewModel(SnapshotSession session, SnapshotHandle snapshot, string typeName, string? module, InstanceScope scope, ulong sameValueAs, IUiDispatcher dispatcher, ILogger? logger = null, INavigator? navigator = null)
        : base(InstanceColumns.Bytes, dispatcher, logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
        Module = module;
        Scope = scope;
        SameValueAs = sameValueAs;

        // The links of this subject (2.3.7 review M8: built here from the navigator, not set from outside).
        OpenRowCommand = new RelayCommand<InstanceRowItem?>(row =>
        {
            if (row is { IsLoaded: true, Address: not 0 })
            {
                navigator?.Navigate(NavigationTarget.Object(Snapshot, row.Address));
            }
        });
        ReferencedByCommand = new RelayCommand(() => navigator?.Navigate(NavigationTarget.TypeReferrers(Snapshot, TypeName, Module)));

        // Why alive and the retained set explain all objects of a type, so a scoped list (review L12: equal strings, LOH)
        // does not offer them.
        WhyAliveCommand = new RelayCommand(() => navigator?.Navigate(NavigationTarget.TypePaths(Snapshot, TypeName, Module)), () => Scope == InstanceScope.All);
        RetainedBySetCommand = new RelayCommand(() => navigator?.Navigate(NavigationTarget.RetainedBySet(Snapshot, TypeName, Module)), () => Scope == InstanceScope.All);
    }

    public SnapshotHandle Snapshot { get; }

    public string TypeName { get; }

    public string? Module { get; }

    public InstanceScope Scope { get; }

    /// <summary>With <see cref="InstanceScope.SameValue"/>: the string whose equal copies are listed.</summary>
    public ulong SameValueAs { get; }

    /// <summary>The navigation title: the type, with its module and scope.</summary>
    public string Title => (string.IsNullOrEmpty(Module) ? TypeName : $"{TypeName} [{Module}]") + Scope switch
    {
        InstanceScope.LargeObjectHeap => " on the large object heap",
        InstanceScope.Finalizable => " to be finalized",
        InstanceScope.Reachable => " reachable from a root",
        InstanceScope.SameValue => string.Create(CultureInfo.InvariantCulture, $" equal to 0x{SameValueAs:x}"),
        _ => string.Empty,
    };

    protected override bool CanQuery => true;

    protected override string BusyText => "Listing objects...";

    protected override string EmptyText => string.IsNullOrWhiteSpace(Filter) ? "No objects of this type." : "No object matches the filter.";

    /// <summary>Opens who references this type's objects (sprint 2.1).</summary>
    public System.Windows.Input.ICommand ReferencedByCommand { get; }

    /// <summary>Opens why this type's objects are alive (sprint 2.2).</summary>
    public System.Windows.Input.ICommand WhyAliveCommand { get; }

    /// <summary>Opens what this type's objects retain together, exactly (sprint 2.4).</summary>
    public System.Windows.Input.ICommand RetainedBySetCommand { get; }

    /// <summary>Runs the first query (after the navigator wires the drill-down command).</summary>
    public void Start()
    {
        _started = true;
        Requery();
    }

    protected override bool SortsDescendingFirst(string column) => column is InstanceColumns.Bytes or InstanceColumns.Retained;

    partial void OnShowRetainedChanged(bool value)
    {
        if (_retainedOffQuietly)
        {
            return;
        }

        if (!value && SortColumn == InstanceColumns.Retained)
        {
            SortColumn = InstanceColumns.Bytes;
            Descending = true;
        }

        if (_started)
        {
            Requery();
        }
    }

    protected override async Task<GridQuery<InstanceRowItem>> RunQueryAsync(CancellationToken cancellationToken)
    {
        var retained = ShowRetained;
        var finished = false;
        var progress = retained ? new Progress<JobProgress>(p => Status = finished || cancellationToken.IsCancellationRequested ? Status : ReferrersText.Progress(p)) : null;
        string? retainedFailure = null;
        InstanceList list;
        try
        {
            list = await _session.QueryInstancesAsync(Snapshot, new InstancesQuery(TypeName, Module, Filter, SortColumn, Descending, Scope, SameValueAs, retained), progress, cancellationToken);
        }
        catch (WorkerOperationException ex) when (retained && !cancellationToken.IsCancellationRequested)
        {
            // Review M8: retained sizes are optional, so the objects load without them and the note says why.
            retainedFailure = ex.Message;
            var bytes = SortColumn == InstanceColumns.Retained;
            list = await _session.QueryInstancesAsync(Snapshot, new InstancesQuery(TypeName, Module, Filter, bytes ? InstanceColumns.Bytes : SortColumn, bytes || Descending, Scope, SameValueAs), null, cancellationToken);
        }

        finished = true;
        var result = list.Result;
        return new GridQuery<InstanceRowItem>(
            list.Rows,
            () =>
            {
                RetainedColumnShown = result.Dominators is not null;
                RetainedNote = result.Dominators is { } dominators ? RetainedText.Source(dominators) + " " + RetainedText.ObjectsRetain : null;
                if (retainedFailure is not null)
                {
                    _retainedOffQuietly = true;
                    ShowRetained = false;
                    _retainedOffQuietly = false;
                    if (SortColumn == InstanceColumns.Retained)
                    {
                        SortColumn = InstanceColumns.Bytes;
                        Descending = true;
                    }

                    RetainedNote = RetainedText.NotShown(retainedFailure);
                }

                ValuesExplanation = result.ValuesAvailable
                    ? result.FilterNote
                    : CapabilityDescriptions.ExplainMissing(SnapshotFeatures.StringContents, Snapshot.Descriptor.Kind);
                Totals = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{result.Rows.TotalCount:N0} of {result.TotalObjects:N0} objects, {Format.Bytes(result.TotalBytes)}{(result.MatchedTypes > 1 ? $" ({result.MatchedTypes} loaded copies of the type)" : string.Empty)}");
            },
            onError => InstanceRowItem.CreateList(list.Rows, result.Rows, onError, Logger));
    }
}
