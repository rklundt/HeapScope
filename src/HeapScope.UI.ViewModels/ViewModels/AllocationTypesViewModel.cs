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
using HeapScope.Application.Allocations;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;

/// <summary>The types allocated in an interval (sprint 2.5), most sampled bytes first; selecting one narrows the trees and stacks.</summary>
public sealed partial class AllocationTypesViewModel : GridQueryViewModel<CellRowItem>
{
    private readonly AllocationSession _session;

    [ObservableProperty]
    private TraceHandle? _trace;

    [ObservableProperty]
    private AllocationScope _scope = AllocationScope.All;

    [ObservableProperty]
    private IReadOnlyList<(ResultColumn Column, int Index)> _columns = [];

    [ObservableProperty]
    private CellRowItem? _selectedRow;

    public AllocationTypesViewModel(AllocationSession session, IUiDispatcher dispatcher, ILogger<AllocationTypesViewModel>? logger)
        : base(AllocationTypeColumns.Bytes, dispatcher, logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>The selected row's type, or null.</summary>
    public string? SelectedType => SelectedRow?.IsLoaded == true ? SelectedRow[AllocationTypeColumns.Type] : null;

    /// <summary>Raised when the selected type changes (the tab narrows the trees and stacks to it).</summary>
    public event EventHandler? SelectedTypeChanged;

    protected override bool CanQuery => Trace is not null;

    protected override string BusyText => "Listing the types allocated...";

    protected override string EmptyText => "No allocation was sampled in this interval.";

    /// <summary>Lists the types of <paramref name="scope"/>'s interval (its type is ignored: the list is what one picks from).</summary>
    public void Show(TraceHandle? trace, AllocationScope scope)
    {
        Trace = trace;
        Scope = scope ?? AllocationScope.All;
        Requery();
    }

    partial void OnSelectedRowChanged(CellRowItem? value) => SelectedTypeChanged?.Invoke(this, EventArgs.Empty);

    protected override async Task<GridQuery<CellRowItem>> RunQueryAsync(CancellationToken cancellationToken)
    {
        var answer = await _session.QueryTypesAsync(Trace!, Scope with { TypeName = null }, cancellationToken);
        var result = answer.Result;
        return new GridQuery<CellRowItem>(
            answer.Rows,
            () =>
            {
                Columns = [.. result.Types.Columns.Select((c, i) => (c, i))];
                Totals = string.Create(CultureInfo.CurrentCulture, $"{result.Types.TotalCount:N0} types, about {Format.Bytes(result.SampledBytes)} in {result.Samples:N0} samples");
            },
            onError => CellRowItem.CreateList(answer.Rows, result.Types, onError, Logger));
    }

    protected override void OnCleared() => SelectedRow = null;
}
