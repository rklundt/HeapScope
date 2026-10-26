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
using HeapScope.Application.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.UI.Wpf.ViewModels;

/// <summary>
/// The query lifecycle every worker-backed grid shares (sprint 1.3, split in 1.5): a debounced filter and column-name
/// sorting re-query the worker; only the latest query owns the view (an older query's result, error or page is dropped);
/// a replaced result set is released. <see cref="TypeGridViewModel{TRow}"/> adds grouping for the type grids.
/// </summary>
public abstract partial class GridQueryViewModel<TRow> : ObservableObject
    where TRow : class, IIndexedRow
{
    /// <summary>Delay between the last filter keystroke and the query.</summary>
    public static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(250);

    private ResultSetHandle? _current;
    private CancellationTokenSource? _query;
    private CancellationTokenSource? _filterDelay;

    [ObservableProperty]
    private VirtualRowList<TRow> _rows = EmptyRows();

    [ObservableProperty]
    private string? _filter;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _totals;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortState))]
    private string _sortColumn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortState))]
    private bool _descending = true;

    protected GridQueryViewModel(string sortColumn, ILogger? logger)
    {
        _sortColumn = sortColumn ?? throw new ArgumentNullException(nameof(sortColumn));
        Logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The query in progress, if any (tests and smoke runs await it).</summary>
    public Task QueryTask { get; private set; } = Task.CompletedTask;

    /// <summary>Opens a row as a new subject (double-click or Enter); set by whoever hosts the grid.</summary>
    public System.Windows.Input.ICommand? OpenRowCommand { get; set; }

    /// <summary>
    /// The sorted column's <c>SortMemberPath</c> and direction ("Bytes|desc"); the grid shows its header arrow from this,
    /// so the arrow always matches the order the worker used.
    /// </summary>
    public string SortState => SortColumn + (Descending ? "|desc" : "|asc");

    protected ILogger Logger { get; }

    /// <summary>Whether there is something to query.</summary>
    protected abstract bool CanQuery { get; }

    /// <summary>Text while a query runs.</summary>
    protected abstract string BusyText { get; }

    /// <summary>Text when a query returns no rows.</summary>
    protected virtual string EmptyText => string.IsNullOrWhiteSpace(Filter) ? "Nothing to show." : "Nothing matches the filter.";

    /// <summary>Releases the grid's current set (the grid left the navigation history).</summary>
    public void Close() => ClearResults();

    /// <summary>Runs the query with the current settings; the result is applied only if it is still the latest.</summary>
    protected abstract Task<GridQuery<TRow>> RunQueryAsync(CancellationToken cancellationToken);

    /// <summary>Whether a column is first sorted largest first; names and text sort ascending.</summary>
    protected virtual bool SortsDescendingFirst(string column) => true;

    /// <summary>Runs the query again with the current settings (a no-op when there is nothing to query).</summary>
    protected void Requery()
    {
        if (!CanQuery)
        {
            return;
        }

        CancelPending();
        ReplaceSource(ref _query);
        QueryTask = QueryAsync(_query!.Token);
    }

    /// <summary>Clears the grid: cancels any query, releases the current set and empties the rows.</summary>
    protected void ClearResults()
    {
        CancelPending();
        Release(_current);
        _current = null;
        Rows.Cancel();
        Rows = EmptyRows();
        Totals = null;
        Status = null;
        OnCleared();
    }

    /// <summary>Clears results specific to the grid (bars, explanations).</summary>
    protected virtual void OnCleared()
    {
    }

    /// <summary>Sorts by a grid column (its result column name): a second click on the same column flips the direction.</summary>
    [RelayCommand]
    private void SortBy(string? column)
    {
        if (string.IsNullOrEmpty(column))
        {
            return;
        }

        if (column == SortColumn)
        {
            Descending = !Descending;
        }
        else
        {
            SortColumn = column;
            Descending = SortsDescendingFirst(column);
        }

        Requery();
    }

    partial void OnFilterChanged(string? value)
    {
        ReplaceSource(ref _filterDelay);
        var token = _filterDelay!.Token;
        _ = Task.Delay(FilterDelay, token).ContinueWith(
            _ => Requery(),
            token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskSchedulerExtensions.FromCurrentSynchronizationContextOrDefault());
    }

    private static VirtualRowList<TRow> EmptyRows() =>
        new(null, 0, _ => throw new InvalidOperationException("An empty list has no rows."), (_, _) => { });

    private void CancelPending()
    {
        // A grouping or sort change also runs a pending filter now, so the debounce must not fire it a second time.
        _filterDelay?.Cancel();
        _filterDelay?.Dispose();
        _filterDelay = null;
        _query?.Cancel();
    }

    private static void ReplaceSource(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = new CancellationTokenSource();
    }

    private async Task QueryAsync(CancellationToken cancellationToken)
    {
        Status = BusyText;
        try
        {
            var query = await RunQueryAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                await query.Handle.ReleaseAsync(CancellationToken.None);
                return;
            }

            var previous = _current;
            _current = query.Handle;
            Release(previous);
            var rows = query.CreateRows(ex => Status = PageError(ex));
            await rows.PreloadAsync();

            // Let a pending render (the page or panel that started this query appearing) run first, so its layout and the
            // grid's rows are two dispatcher operations rather than one twice as long (sprint 1.5 smoke runs).
            await TaskSchedulerExtensions.YieldToRenderAsync();
            if (cancellationToken.IsCancellationRequested)
            {
                // A newer query took over while the first page loaded; it releases this set as the previous one.
                rows.Cancel();
                return;
            }

            query.Apply();
            Rows.Cancel();
            Rows = rows;
            Status = query.Handle.Info.TotalCount == 0 ? EmptyText : null;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // A newer query (or clearing the grid) replaced this one; whatever it failed with is stale.
        }
        catch (Exception ex) when (ex is WorkerStartException or WorkerCrashedException or WorkerOperationException or OperationCanceledException)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "A {Grid} query failed", GetType().Name);
            Status = "The query failed. Details are in the log; try again, or reopen the snapshot.";
        }
    }

    private static string PageError(Exception ex) =>
        ex is WorkerStartException or WorkerCrashedException or WorkerOperationException
            ? ex.Message
            : "Some rows could not be loaded. Details are in the log; scroll to retry.";

    private void Release(ResultSetHandle? handle) =>
        _ = handle?.ReleaseAsync(CancellationToken.None).ContinueWith(
            t => Logger.LogDebug(t.Exception, "Releasing a previous result set failed"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}

/// <summary>
/// A finished grid query: its result set, what to apply to the view model (totals, bars), and how to build its rows.
/// </summary>
public sealed record GridQuery<TRow>(ResultSetHandle Handle, Action Apply, Func<Action<Exception>, VirtualRowList<TRow>> CreateRows)
    where TRow : class, IIndexedRow;

/// <summary>Scheduler helper: the dispatcher's context when there is one (the app), the thread pool otherwise (tests).</summary>
internal static class TaskSchedulerExtensions
{
    public static TaskScheduler FromCurrentSynchronizationContextOrDefault() =>
        SynchronizationContext.Current is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext();

    /// <summary>
    /// How the UI continues after pending render and input work, set by the app at start (the WPF app yields at the
    /// dispatcher's background priority); unset (tests), a query continues at once. Kept out of the view models so they
    /// carry no UI framework type (sprint 2.3.5, ahead of the Avalonia port).
    /// </summary>
    public static Func<Task>? RenderYield { get; set; }

    /// <summary>Continues after pending render and input work when the app says how (<see cref="RenderYield"/>).</summary>
    public static Task YieldToRenderAsync() => RenderYield?.Invoke() ?? Task.CompletedTask;
}
