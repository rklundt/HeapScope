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

using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application.Allocations;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;

/// <summary>
/// The back traces or call tree of an allocation trace's interval (sprint 2.5): served flattened by the worker from the
/// open rows like the dominator tree (ADR-027); expanding or collapsing a row queries again and keeps that row selected.
/// </summary>
public sealed partial class AllocationTreeViewModel : GridQueryViewModel<AllocationRowItem>
{
    private readonly AllocationSession _session;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private string? _revealKey;
    private string? _revealAsked;
    private int _revealIndex = -1;
    private int _revealFallback = -1;
    private int _fallbackAsked = -1;

    [ObservableProperty]
    private TraceHandle? _trace;

    [ObservableProperty]
    private AllocationScope _scope = AllocationScope.All;

    [ObservableProperty]
    private AllocationTreeKindDto _kind = AllocationTreeKindDto.BackTraces;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExpandCommand), nameof(CollapseCommand))]
    private AllocationRowItem? _selectedRow;

    [ObservableProperty]
    private string? _note;

    public AllocationTreeViewModel(AllocationSession session, IUiDispatcher dispatcher, ILogger<AllocationTreeViewModel>? logger)
        : base(AllocationTreeColumns.Total, dispatcher, logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        OpenRowCommand = ToggleCommand;
    }

    /// <summary>The open rows' keys (tests).</summary>
    public IReadOnlyCollection<string> Expanded => _expanded;

    /// <summary>The selection of the row a query revealed (tests and smoke runs await it).</summary>
    public Task SelectTask { get; private set; } = Task.CompletedTask;

    protected override bool CanQuery => Trace is not null;

    protected override string BusyText => Kind == AllocationTreeKindDto.CallTree ? "Building the call tree..." : "Building the back traces...";

    protected override string EmptyText => "No allocation was sampled in this interval.";

    /// <summary>Shows a trace's tree for <paramref name="scope"/>, every row closed.</summary>
    public void Show(TraceHandle? trace, AllocationScope scope)
    {
        ClearResults();
        _expanded.Clear();
        Trace = trace;
        Scope = scope ?? AllocationScope.All;
        Requery();
    }

    /// <summary>Queries again for another interval or type; open rows stay open where they still exist.</summary>
    public void Narrow(AllocationScope scope)
    {
        Scope = scope ?? AllocationScope.All;
        Requery();
    }

    partial void OnKindChanged(AllocationTreeKindDto value)
    {
        // The two trees' keys differ: a switch starts closed.
        using var activity = Act("show the {0}", value);
        ClearResults();
        _expanded.Clear();
        Requery();
    }

    protected override async Task<GridQuery<AllocationRowItem>> RunQueryAsync(CancellationToken cancellationToken)
    {
        var finished = false;
        var progress = new Progress<JobProgress>(p => Status = finished || cancellationToken.IsCancellationRequested ? Status : ReferrersText.Progress(p));
        var reveal = _revealKey;
        var fallback = _revealFallback;
        _revealKey = null;
        _revealFallback = -1;
        var answer = await _session.QueryTreeAsync(Trace!, Scope, Kind, [.. _expanded], reveal, progress, cancellationToken);
        finished = true;
        var result = answer.Result;
        return new GridQuery<AllocationRowItem>(
            answer.Rows,
            () =>
            {
                Totals = string.Create(CultureInfo.CurrentCulture, $"About {Format.Bytes(result.SampledBytes)} in {result.Samples:N0} samples");
                Note = result.Truncated
                    ? string.Create(CultureInfo.CurrentCulture, $"The tree is cut at {AllocationTreeLimits.MaxRows:N0} rows; collapse rows to see the rest.")
                    : null;
                (_revealAsked, _revealIndex, _fallbackAsked) = (reveal, result.RevealIndex, fallback);
            },
            onError => AllocationRowItem.CreateList(answer.Rows, result.Rows, onError, Logger));
    }

    protected override void OnRowsApplied()
    {
        var (key, index, fallback) = (_revealAsked, _revealIndex, _fallbackAsked);
        (_revealAsked, _revealIndex, _fallbackAsked) = (null, -1, -1);
        if (key is null)
        {
            return;
        }

        if (index >= 0 || (fallback >= 0 && fallback < Rows.Count))
        {
            // An expanded "more" row is replaced by the next batch, whose first row takes its place.
            SelectTask = SelectAsync(Rows, index >= 0 ? index : fallback);
        }
        else
        {
            // The row is gone in this interval (no sample went through it): it is not kept open.
            _expanded.Remove(key);
        }
    }

    protected override void OnCleared()
    {
        SelectedRow = null;
        Note = null;
        (_revealKey, _revealAsked, _revealIndex, _revealFallback, _fallbackAsked) = (null, null, -1, -1, -1);
    }

    /// <summary>Expands a closed row or collapses an open one (its arrow, Enter, a double-click).</summary>
    [RelayCommand]
    private void Toggle(AllocationRowItem? row)
    {
        // The open keys decide, not the row, whose state may be a query behind.
        if (row is { CanExpand: true })
        {
            SetExpanded(row, !_expanded.Contains(row.Key));
        }
    }

    [RelayCommand(CanExecute = nameof(CanExpandRow))]
    private void Expand(AllocationRowItem? row)
    {
        if (CanExpandRow(row))
        {
            SetExpanded(row!, true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCollapseRow))]
    private void Collapse(AllocationRowItem? row)
    {
        if (CanCollapseRow(row))
        {
            SetExpanded(row!, false);
        }
    }

    private bool CanExpandRow(AllocationRowItem? row) => row is { CanExpand: true } && !_expanded.Contains(row.Key);

    private bool CanCollapseRow(AllocationRowItem? row) => row is not null && _expanded.Contains(row.Key);

    private void SetExpanded(AllocationRowItem row, bool expanded)
    {
        using var activity = Act(expanded ? "expand {0}" : "collapse {0}", row.Key + " " + row.Name);
        if (expanded)
        {
            _expanded.Add(row.Key);
        }
        else
        {
            _expanded.Remove(row.Key);
        }

        _revealKey = row.Key;
        _revealFallback = row.Kind == AllocationRowKinds.More ? row.Index : -1;
        Requery();
    }

    /// <summary>Starts a user action: its log line, under an activity its queries join.</summary>
    private Activity? Act(string action, object argument)
    {
        var activity = new Activity("HeapScope.Allocations").Start();
        Logger.LogInformation("Allocations {Kind}: {Action}", Kind, string.Format(CultureInfo.InvariantCulture, action, argument));
        return activity;
    }

    private async Task SelectAsync(VirtualRowList<AllocationRowItem> rows, int index)
    {
        var row = rows.GetRow(index);
        try
        {
            await rows.WhenLoadedAsync();
        }
        catch (Exception ex) when (ex is HeapScope.Application.Workers.WorkerException or OperationCanceledException)
        {
            return;
        }

        if (ReferenceEquals(Rows, rows) && row.IsLoaded)
        {
            SelectedRow = row;
        }
    }
}
