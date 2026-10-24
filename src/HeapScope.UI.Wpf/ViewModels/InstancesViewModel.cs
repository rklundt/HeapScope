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
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.Wpf.ViewModels;

/// <summary>One object in the instances grid, filled when its page arrives.</summary>
public sealed class InstanceRowItem(int index) : ObservableObject, IIndexedRow
{
    public int Index { get; } = index;

    public ulong Address { get; private set; }

    public string AddressText { get; private set; } = string.Empty;

    public string Type { get; private set; } = string.Empty;

    public long Bytes { get; private set; }

    public string Generation { get; private set; } = string.Empty;

    public string Value { get; private set; } = string.Empty;

    public bool IsLoaded { get; private set; }

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
        IsLoaded = true;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// The objects of a type (sprint 1.5, dotMemory's instances view): sortable by any column, filtered by address prefix
/// (<c>0x...</c>), regular expression (<c>/.../</c>) or text in the value; opened from a type or compare row.
/// </summary>
public sealed partial class InstancesViewModel : GridQueryViewModel<InstanceRowItem>
{
    private readonly SnapshotSession _session;

    [ObservableProperty]
    private string? _valuesExplanation;

    public InstancesViewModel(SnapshotSession session, SnapshotHandle snapshot, string typeName, string? module, ILogger? logger = null)
        : this(session, snapshot, typeName, module, InstanceScope.All, 0, logger)
    {
    }

    /// <summary>The objects of a type within a scope (sprint 1.6: what an inspection row opens).</summary>
    public InstancesViewModel(SnapshotSession session, SnapshotHandle snapshot, string typeName, string? module, InstanceScope scope, ulong sameValueAs, ILogger? logger = null)
        : base(InstanceColumns.Bytes, logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
        Module = module;
        Scope = scope;
        SameValueAs = sameValueAs;
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
        InstanceScope.SameValue => string.Create(CultureInfo.InvariantCulture, $" equal to 0x{SameValueAs:x}"),
        _ => string.Empty,
    };

    protected override bool CanQuery => true;

    protected override string BusyText => "Listing objects...";

    protected override string EmptyText => string.IsNullOrWhiteSpace(Filter) ? "No objects of this type." : "No object matches the filter.";

    /// <summary>Opens who references this type's objects (sprint 2.1); set by the navigator.</summary>
    public System.Windows.Input.ICommand? ReferencedByCommand { get; set; }

    /// <summary>Runs the first query (after the navigator wires the drill-down command).</summary>
    public void Start() => Requery();

    protected override bool SortsDescendingFirst(string column) => column == InstanceColumns.Bytes;

    protected override async Task<GridQuery<InstanceRowItem>> RunQueryAsync(CancellationToken cancellationToken)
    {
        var list = await _session.QueryInstancesAsync(Snapshot, new InstancesQuery(TypeName, Module, Filter, SortColumn, Descending, Scope, SameValueAs), null, cancellationToken);
        var result = list.Result;
        return new GridQuery<InstanceRowItem>(
            list.Rows,
            () =>
            {
                ValuesExplanation = result.ValuesAvailable
                    ? null
                    : CapabilityDescriptions.ExplainMissing(SnapshotFeatures.StringContents, Snapshot.Descriptor.Kind);
                Totals = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{result.Rows.TotalCount:N0} of {result.TotalObjects:N0} objects, {Format.Bytes(result.TotalBytes)}{(result.MatchedTypes > 1 ? $" ({result.MatchedTypes} loaded copies of the type)" : string.Empty)}");
            },
            onError => InstanceRowItem.CreateList(list.Rows, result.Rows, onError, Logger));
    }
}
