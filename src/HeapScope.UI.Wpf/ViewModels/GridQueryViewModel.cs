using CommunityToolkit.Mvvm.ComponentModel;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.UI.Wpf.ViewModels;

/// <summary>
/// The query lifecycle shared by the types grid and the comparison grid (sprint 1.3, shared since 1.4): grouping and a
/// debounced filter re-query the worker; only the latest query owns the view (an older query's result, error or page is
/// dropped); a replaced result set is released.
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
    private TypeGrouping _groupBy = TypeGrouping.Type;

    [ObservableProperty]
    private string? _filter;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _totals;

    protected GridQueryViewModel(ILogger? logger)
    {
        Logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The groupings the selector offers.</summary>
    public IReadOnlyList<TypeGrouping> Groupings { get; } = Enum.GetValues<TypeGrouping>();

    /// <summary>The query in progress, if any (tests and smoke runs await it).</summary>
    public Task QueryTask { get; private set; } = Task.CompletedTask;

    protected ILogger Logger { get; }

    /// <summary>Whether there is something to query (a snapshot, or two).</summary>
    protected abstract bool CanQuery { get; }

    /// <summary>Text while a query runs.</summary>
    protected abstract string BusyText { get; }

    protected static string Plural(TypeGrouping grouping) => grouping switch
    {
        TypeGrouping.Namespace => "namespaces",
        TypeGrouping.Assembly => "assemblies",
        _ => "types",
    };

    protected static string Singular(TypeGrouping grouping) => grouping switch
    {
        TypeGrouping.Namespace => "namespace",
        TypeGrouping.Assembly => "assembly",
        _ => "type",
    };

    /// <summary>Runs the query with the current settings; the result is applied only if it is still the latest.</summary>
    protected abstract Task<GridQuery<TRow>> RunQueryAsync(CancellationToken cancellationToken);

    /// <summary>An empty list of this grid's rows (it never creates a row).</summary>
    private static VirtualRowList<TRow> EmptyRows() =>
        new(null, 0, _ => throw new InvalidOperationException("An empty list has no rows."), (_, _) => { });

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

    partial void OnGroupByChanged(TypeGrouping value) => Requery();

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
            query.Apply();
            Rows.Cancel();
            Rows = query.CreateRows(ex => Status = PageError(ex));
            Status = query.Handle.Info.TotalCount == 0 ? $"No {Singular(GroupBy)} matches the filter." : null;
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
            Logger.LogError(ex, "A grid query failed");
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
}
