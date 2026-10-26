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

using System.Collections;
using System.Collections.Specialized;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.UI.ViewModels;

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

    /// <summary>Pages kept loaded (10,000 rows); farther pages are dropped and fetched again when needed (sprint 1.5).</summary>
    public const int MaxPages = 50;
}

/// <summary>
/// A list of a result set's rows for a virtualizing grid (sprint 1.3, generic since 1.4): the count is known up front,
/// rows are created when the grid asks for them and filled by fetching their page from the worker. At most
/// <see cref="VirtualRowList.MaxPages"/> pages stay loaded: the pages farthest from the last one asked for are dropped
/// (rows on screen are always near it), so a list of millions of instances holds a few thousand rows. <see cref="Cancel"/> ends the list when a newer query replaces it: page requests in flight
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
    private int _lastPage;

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
        _lastPage = page;
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

            DropFarPages();
        }

        return item;
    }

    /// <summary>
    /// Fetches the first page before a grid shows the list, so its rows are laid out once, already filled, instead of
    /// laid out empty and again when the page arrives (sprint 1.5: the second pass doubled the work after a query).
    /// </summary>
    public Task PreloadAsync()
    {
        if (Count > 0)
        {
            GetRow(0);
        }

        return WhenLoadedAsync();
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
            if (token.IsCancellationRequested || !_pages.ContainsKey(page))
            {
                // Replaced by a newer query, or dropped from the window while loading.
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
            if (ex is not (WorkerException))
            {
                _logger.LogError(ex, "Loading rows {Start} to {End} of a grid failed", start, start + VirtualRowList.PageSize - 1);
            }

            _onError?.Invoke(ex);
        }
    }

    private void DropFarPages()
    {
        while (_pages.Count > VirtualRowList.MaxPages)
        {
            var farthest = _pages.Keys.MaxBy(p => Math.Abs(p - _lastPage));
            _pages.Remove(farthest);
            var start = farthest * VirtualRowList.PageSize;
            var end = Math.Min(Count, start + VirtualRowList.PageSize);
            for (var i = start; i < end; i++)
            {
                _items.Remove(i);
            }
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
