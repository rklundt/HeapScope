using System.Collections;
using System.Collections.Specialized;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.UI.Wpf.ViewModels;

/// <summary>A grid row that knows its position, so a row list finds it without a scan.</summary>
public interface IIndexedRow
{
    int Index { get; }
}

/// <summary>Constants shared by every <see cref="VirtualRowList{TRow}"/>.</summary>
public static class VirtualRowList
{
    /// <summary>Rows per page fetched from the worker.</summary>
    public const int PageSize = 200;
}

/// <summary>
/// A list of a result set's rows for a virtualizing grid (sprint 1.3, generic since 1.4): the count is known up front,
/// rows are created when the grid asks for them and filled by fetching their page from the worker. Rows the grid has
/// asked for are kept for the life of the list (one list per query); a bounded window of pages is a backlog item for
/// the object lists of 1.5. <see cref="Cancel"/> ends the list when a newer query replaces it: page requests in flight
/// stop, and their rows and errors are dropped.
/// </summary>
public sealed class VirtualRowList<TRow> : IList, INotifyCollectionChanged
    where TRow : class, IIndexedRow
{
    private readonly ResultSetHandle? _rows;
    private readonly Func<int, TRow> _create;
    private readonly Action<TRow, IReadOnlyList<string?>> _fill;
    private readonly Dictionary<int, TRow> _items = [];
    private readonly Dictionary<int, Task> _pages = [];
    private readonly Action<Exception>? _onError;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();

    /// <param name="rows">The result set, or null for an empty list.</param>
    /// <param name="count">Rows in the result set.</param>
    /// <param name="create">Creates the unloaded row at an index.</param>
    /// <param name="fill">Fills a row from its cells.</param>
    /// <param name="onError">Called on the dispatcher when a page fails (the page is retried when asked for again).</param>
    /// <param name="logger">Logs unexpected page failures.</param>
    public VirtualRowList(
        ResultSetHandle? rows,
        int count,
        Func<int, TRow> create,
        Action<TRow, IReadOnlyList<string?>> fill,
        Action<Exception>? onError = null,
        ILogger? logger = null)
    {
        _rows = rows;
        Count = count;
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _fill = fill ?? throw new ArgumentNullException(nameof(fill));
        _onError = onError;
        _logger = logger ?? NullLogger.Instance;
    }

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
    public TRow GetRow(int index)
    {
        var item = GetRowWithoutFetch(index);
        var page = index / VirtualRowList.PageSize;
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

    public bool Contains(object? value) => IndexOf(value) >= 0;

    // WPF calls this on selection and currency changes: constant time through the row's own index.
    public int IndexOf(object? value) =>
        value is TRow row && _items.TryGetValue(row.Index, out var same) && ReferenceEquals(same, row) ? row.Index : -1;

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
            array.SetValue(_items.TryGetValue(i, out var item) ? item : _create(i), index + i);
        }
    }

    public IEnumerator GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return _items.TryGetValue(i, out var item) ? item : _create(i);
        }
    }

    private async Task LoadPageAsync(int page)
    {
        var start = page * VirtualRowList.PageSize;
        var token = _lifetime.Token;
        try
        {
            var result = await _rows!.GetRowsAsync(start, Math.Min(VirtualRowList.PageSize, Count - start), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            for (var i = 0; i < result.Rows.Count; i++)
            {
                _fill(GetRowWithoutFetch(start + i), result.Rows[i]);
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
                _logger.LogError(ex, "Loading rows {Start} to {End} of a grid failed", start, start + VirtualRowList.PageSize - 1);
            }

            _onError?.Invoke(ex);
        }
    }

    private TRow GetRowWithoutFetch(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        if (!_items.TryGetValue(index, out var item))
        {
            item = _create(index);
            _items[index] = item;
        }

        return item;
    }
}

/// <summary>Finds result columns by name; a missing required column means the worker and the app differ in build.</summary>
public static class ResultColumns
{
    public static int Find(IReadOnlyList<ResultColumn> columns, string name)
    {
        ArgumentNullException.ThrowIfNull(columns);
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    public static int Require(IReadOnlyList<ResultColumn> columns, string name) => Find(columns, name) is >= 0 and var index
        ? index
        : throw new InvalidOperationException($"The worker's result has no {name} column. The worker and the app are from different builds.");
}
