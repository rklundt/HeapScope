using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.UI.Wpf.ViewModels;

/// <summary>
/// Where each value of a type summary row is, found from the result set's column names, so a change in the worker's
/// column order cannot put values under the wrong header. Name, Objects and Bytes are required.
/// </summary>
public sealed class TypeRowLayout
{
    private TypeRowLayout(IReadOnlyList<ResultColumn> columns)
    {
        int Find(string name) => columns.Select((c, i) => (c, i)).FirstOrDefault(x => x.c.Name == name, (null!, -1)).Item2;
        int Require(string name) => Find(name) is >= 0 and var i
            ? i
            : throw new InvalidOperationException($"The worker's type summary has no {name} column. The worker and the app are from different builds.");

        Name = Require("Name");
        Objects = Require("Objects");
        Bytes = Require("Bytes");
        Percent = Find("Percent");
        Gen0 = Find("Gen0");
        Gen1 = Find("Gen1");
        Gen2 = Find("Gen2");
        LargeObjectHeap = Find("LargeObjectHeap");
        PinnedObjectHeap = Find("PinnedObjectHeap");
        Frozen = Find("Frozen");
    }

    public int Name { get; }

    public int Objects { get; }

    public int Bytes { get; }

    public int Percent { get; }

    public int Gen0 { get; }

    public int Gen1 { get; }

    public int Gen2 { get; }

    public int LargeObjectHeap { get; }

    public int PinnedObjectHeap { get; }

    public int Frozen { get; }

    public static TypeRowLayout From(IReadOnlyList<ResultColumn> columns) => new(columns ?? throw new ArgumentNullException(nameof(columns)));
}

/// <summary>
/// One row of the types grid, filled when its page arrives (data virtualization). Values are set together and announced
/// with one "all properties changed" notification: a page fills 200 rows, and a notification per value made the grid's
/// rebinding exceed the 100 ms dispatcher budget (sprint 1.3 smoke run).
/// </summary>
public sealed class TypeRowItem(int index) : ObservableObject
{
    public int Index { get; } = index;

    public string Name { get; private set; } = string.Empty;

    public long Objects { get; private set; }

    public long Bytes { get; private set; }

    public double Percent { get; private set; }

    public long Gen0 { get; private set; }

    public long Gen1 { get; private set; }

    public long Gen2 { get; private set; }

    public long LargeObjectHeap { get; private set; }

    public long PinnedObjectHeap { get; private set; }

    public long Frozen { get; private set; }

    public bool IsLoaded { get; private set; }

    internal void Fill(IReadOnlyList<string?> cells, TypeRowLayout layout)
    {
        Name = layout.Name < cells.Count ? cells[layout.Name] ?? string.Empty : string.Empty;
        Objects = ParseLong(cells, layout.Objects);
        Bytes = ParseLong(cells, layout.Bytes);
        Percent = layout.Percent >= 0 && layout.Percent < cells.Count && double.TryParse(cells[layout.Percent], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
        Gen0 = ParseLong(cells, layout.Gen0);
        Gen1 = ParseLong(cells, layout.Gen1);
        Gen2 = ParseLong(cells, layout.Gen2);
        LargeObjectHeap = ParseLong(cells, layout.LargeObjectHeap);
        PinnedObjectHeap = ParseLong(cells, layout.PinnedObjectHeap);
        Frozen = ParseLong(cells, layout.Frozen);
        IsLoaded = true;
        OnPropertyChanged(string.Empty);
    }

    private static long ParseLong(IReadOnlyList<string?> cells, int index) =>
        index >= 0 && index < cells.Count && long.TryParse(cells[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

/// <summary>
/// A list of a result set's rows for a virtualizing grid: the count is known up front, rows are created when the grid
/// asks for them and filled by fetching their page from the worker. Rows the grid has asked for are kept for the life
/// of the list (one list per query); a bounded window of pages is a backlog item for the object lists of 1.5.
/// <see cref="Cancel"/> ends the list when a newer query replaces it: page requests in flight stop, and their rows and
/// errors are dropped.
/// </summary>
public sealed class VirtualRowList : IList, INotifyCollectionChanged
{
    /// <summary>Rows per page fetched from the worker.</summary>
    public const int PageSize = 200;

    private readonly ResultSetHandle? _rows;
    private readonly TypeRowLayout? _layout;
    private readonly Dictionary<int, TypeRowItem> _items = [];
    private readonly Dictionary<int, Task> _pages = [];
    private readonly Action<Exception>? _onError;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();

    public VirtualRowList(ResultSetHandle? rows, ResultSetInfo? info, Action<Exception>? onError = null, ILogger? logger = null)
    {
        _rows = rows;
        _layout = info is null ? null : TypeRowLayout.From(info.Columns);
        Count = info?.TotalCount ?? 0;
        _onError = onError;
        _logger = logger ?? NullLogger.Instance;
    }

    public static VirtualRowList Empty { get; } = new(null, null);

    // The count never changes and rows fill in place (each row notifies), so the list never raises this. It implements
    // INotifyCollectionChanged only so WPF does not take a snapshot copy of an IList it believes is static.
    public event NotifyCollectionChangedEventHandler? CollectionChanged
    {
        add { }
        remove { }
    }

    public int Count { get; }

    public bool IsFixedSize => true;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    /// <summary>Pages requested so far (tests).</summary>
    public IReadOnlyCollection<int> RequestedPages => _pages.Keys;

    public object? this[int index]
    {
        get => GetRow(index);
        set => throw new NotSupportedException();
    }

    /// <summary>The row at <paramref name="index"/>; starts fetching its page when it is not loaded yet.</summary>
    public TypeRowItem GetRow(int index)
    {
        var item = GetRowWithoutFetch(index);
        var page = index / PageSize;
        if (_rows is not null && !_lifetime.IsCancellationRequested && !_pages.ContainsKey(page))
        {
            // Reserve the slot first: a load that fails synchronously removes it, and must stay removed so the page can
            // be retried.
            _pages[page] = Task.CompletedTask;
            var load = LoadPageAsync(page);
            if (_pages.ContainsKey(page))
            {
                _pages[page] = load;
            }
        }

        return item;
    }

    /// <summary>Ends the list: page requests in flight stop and their results are dropped.</summary>
    public void Cancel() => _lifetime.Cancel();

    /// <summary>Completes when every page requested so far has arrived (tests and smoke runs).</summary>
    public Task WhenLoadedAsync() => Task.WhenAll(_pages.Values);

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public bool Contains(object? value) => value is TypeRowItem item && item.Index < Count && _items.ContainsKey(item.Index);

    public int IndexOf(object? value) => value is TypeRowItem item && _items.TryGetValue(item.Index, out var same) && ReferenceEquals(same, item) ? item.Index : -1;

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    // Enumeration never fetches: an enumerating consumer (automation peers, a copy command) would otherwise pull the
    // whole result set over the pipe. Rows not asked for by index come back as unloaded placeholders.
    public void CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (var i = 0; i < Count; i++)
        {
            array.SetValue(_items.TryGetValue(i, out var item) ? item : new TypeRowItem(i), index + i);
        }
    }

    public IEnumerator GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return _items.TryGetValue(i, out var item) ? item : new TypeRowItem(i);
        }
    }

    private async Task LoadPageAsync(int page)
    {
        var start = page * PageSize;
        var token = _lifetime.Token;
        try
        {
            var result = await _rows!.GetRowsAsync(start, Math.Min(PageSize, Count - start), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            for (var i = 0; i < result.Rows.Count; i++)
            {
                GetRowWithoutFetch(start + i).Fill(result.Rows[i], _layout!);
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // A newer query replaced this list; its handle may already be released.
        }
        catch (Exception ex)
        {
            // The page can be asked for again (scrolling back retries it).
            _pages.Remove(page);
            if (ex is not (WorkerStartException or WorkerCrashedException or WorkerOperationException))
            {
                _logger.LogError(ex, "Loading rows {Start} to {End} of the types grid failed", start, start + PageSize - 1);
            }

            _onError?.Invoke(ex);
        }
    }

    private TypeRowItem GetRowWithoutFetch(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        if (!_items.TryGetValue(index, out var item))
        {
            item = new TypeRowItem(index);
            _items[index] = item;
        }

        return item;
    }
}

/// <summary>A heap kind in the overview bar: share of the snapshot's bytes, in its fixed color.</summary>
public sealed record HeapKindShare(HeapKindDto Kind, string Label, long Objects, long Bytes, double Fraction)
{
    public string BrushKey => "HeapKind." + Kind;

    public string Text => string.Create(CultureInfo.CurrentCulture, $"{Label} {Format.Bytes(Bytes)} ({Fraction * 100:N1}%)");
}

/// <summary>
/// The types grid of a snapshot (sprint 1.3, dotMemory's types view): grouping, type-to-filter (debounced) and sorting
/// re-query the worker; rows are virtualized; generation columns show only when the snapshot has generations. Only the
/// latest query owns the view: an older query's result, error or page is dropped.
/// </summary>
public sealed partial class TypeSummaryViewModel : ObservableObject
{
    /// <summary>Delay between the last filter keystroke and the query.</summary>
    public static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(250);

    private readonly SnapshotSession _session;
    private readonly ILogger _logger;
    private SnapshotHandle? _snapshot;
    private TypeSummary? _current;
    private CancellationTokenSource? _query;
    private CancellationTokenSource? _filterDelay;

    [ObservableProperty]
    private VirtualRowList _rows = VirtualRowList.Empty;

    [ObservableProperty]
    private TypeGrouping _groupBy = TypeGrouping.Type;

    [ObservableProperty]
    private string? _filter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortState))]
    private TypeSummarySort _sort = TypeSummarySort.Bytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortState))]
    private bool _descending = true;

    [ObservableProperty]
    private bool _hasGenerations = true;

    [ObservableProperty]
    private string? _generationsExplanation;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _totals;

    [ObservableProperty]
    private IReadOnlyList<HeapKindShare> _heapKinds = [];

    public TypeSummaryViewModel(SnapshotSession session, ILogger<TypeSummaryViewModel>? logger = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>The groupings the selector offers.</summary>
    public IReadOnlyList<TypeGrouping> Groupings { get; } = Enum.GetValues<TypeGrouping>();

    /// <summary>The query in progress, if any (tests and smoke runs await it).</summary>
    public Task QueryTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The sorted column's <c>SortMemberPath</c> and direction ("Bytes|desc"); the grid shows its header arrow from this,
    /// so the arrow always matches the order the worker used.
    /// </summary>
    public string SortState => (Sort switch
    {
        TypeSummarySort.Name => nameof(TypeRowItem.Name),
        TypeSummarySort.Count => nameof(TypeRowItem.Objects),
        _ => nameof(TypeRowItem.Bytes),
    }) + (Descending ? "|desc" : "|asc");

    /// <summary>Shows a snapshot (or nothing) and queries it with the current grouping, filter and sort.</summary>
    public void Show(SnapshotHandle? snapshot)
    {
        _snapshot = snapshot;
        if (snapshot is null)
        {
            CancelPending();
            ReplaceCurrent(null);
            Rows.Cancel();
            Rows = VirtualRowList.Empty;
            Totals = null;
            Status = null;
            HeapKinds = [];
            return;
        }

        Requery();
    }

    /// <summary>Sorts by a grid column: a second click on the same column flips the direction.</summary>
    [RelayCommand]
    private void SortBy(string? column)
    {
        var sort = column switch
        {
            nameof(TypeRowItem.Name) => TypeSummarySort.Name,
            nameof(TypeRowItem.Objects) => TypeSummarySort.Count,
            _ => TypeSummarySort.Bytes,
        };

        if (sort == Sort)
        {
            Descending = !Descending;
        }
        else
        {
            Sort = sort;
            Descending = sort != TypeSummarySort.Name;
        }

        Requery();
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

    private void Requery()
    {
        if (_snapshot is null)
        {
            return;
        }

        CancelPending();
        ReplaceSource(ref _query);
        QueryTask = QueryAsync(_snapshot, _query!.Token);
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

    private async Task QueryAsync(SnapshotHandle snapshot, CancellationToken cancellationToken)
    {
        Status = "Summarizing...";
        try
        {
            var summary = await _session.QueryTypeSummaryAsync(snapshot, GroupBy, Filter, Sort, Descending, null, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                await summary.Rows.ReleaseAsync(CancellationToken.None);
                return;
            }

            ReplaceCurrent(summary);
            var result = summary.Result;
            HasGenerations = result.HasGenerations;
            GenerationsExplanation = result.HasGenerations
                ? null
                : CapabilityDescriptions.ExplainMissing(SnapshotFeatures.Generations, snapshot.Descriptor.Kind);
            HeapKinds = [.. result.HeapKinds.Select(k => new HeapKindShare(k.Kind, Label(k.Kind), k.Objects, k.Bytes, result.TotalBytes == 0 ? 0 : (double)k.Bytes / result.TotalBytes))];
            Totals = string.Create(
                CultureInfo.CurrentCulture,
                $"{result.Rows.TotalCount:N0} of {result.UnfilteredRows:N0} {Plural(GroupBy)}; {result.TotalObjects:N0} objects, {Format.Bytes(result.TotalBytes)} (all objects in the dump, unreachable ones included)");
            Rows.Cancel();
            Rows = new VirtualRowList(summary.Rows, result.Rows, ex => Status = PageError(ex), _logger);
            Status = result.Rows.TotalCount == 0 ? $"No {Singular(GroupBy)} matches the filter." : null;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // A newer query (or closing the snapshot) replaced this one; whatever it failed with is stale.
        }
        catch (Exception ex) when (ex is WorkerStartException or WorkerCrashedException or WorkerOperationException or OperationCanceledException)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The type summary query failed");
            Status = "The type summary failed. Details are in the log; try again, or reopen the snapshot.";
        }
    }

    private static string PageError(Exception ex) =>
        ex is WorkerStartException or WorkerCrashedException or WorkerOperationException
            ? ex.Message
            : "Some rows could not be loaded. Details are in the log; scroll to retry.";

    private void ReplaceCurrent(TypeSummary? summary)
    {
        var previous = _current;
        _current = summary;
        if (previous is not null)
        {
            _ = previous.Rows.ReleaseAsync(CancellationToken.None).ContinueWith(
                t => _logger.LogDebug(t.Exception, "Releasing a previous type summary failed"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    private static string Plural(TypeGrouping grouping) => grouping switch
    {
        TypeGrouping.Namespace => "namespaces",
        TypeGrouping.Assembly => "assemblies",
        _ => "types",
    };

    private static string Singular(TypeGrouping grouping) => grouping switch
    {
        TypeGrouping.Namespace => "namespace",
        TypeGrouping.Assembly => "assembly",
        _ => "type",
    };

    private static string Label(HeapKindDto kind) => kind switch
    {
        HeapKindDto.LargeObjectHeap => "LOH",
        HeapKindDto.PinnedObjectHeap => "POH",
        HeapKindDto.Unknown => "Unknown generation",
        _ => kind.ToString(),
    };
}

/// <summary>Scheduler helper: the dispatcher's context when there is one (the app), the thread pool otherwise (tests).</summary>
internal static class TaskSchedulerExtensions
{
    public static TaskScheduler FromCurrentSynchronizationContextOrDefault() =>
        SynchronizationContext.Current is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext();
}
