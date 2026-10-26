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
using System.ComponentModel;
using System.Globalization;
using Avalonia.Collections;

namespace HeapScope.UI.Controls;

/// <summary>
/// The collection view a DataGrid uses for a data-virtualized row list (sprint 2.3.5 prototype, 2.3.6): the grid uses an
/// <see cref="IDataGridCollectionView"/> as it is, where it would wrap any other source in a view that copies every row
/// into a list of its own. Reads go to the row list by index, so only the rows on screen are fetched. Sorting and
/// filtering happen in the worker through the view model; this view never sorts, filters or groups, and its sort
/// descriptions only show the order the worker used (the header arrows).
/// </summary>
/// <remarks>
/// Enumerating or copying the whole list would make a placeholder row per index (1.5 million on the Medium dump); no
/// grid path should do it (the selection is single, so there is no select all). Each enumeration is counted in
/// <see cref="VirtualCollectionViews.Enumerations"/>, which the UI tests check stays at zero.
/// </remarks>
public sealed class VirtualCollectionView<TRow>(IList rows) : IDataGridCollectionView, IList, IEnumerable<TRow>, ISortArrows
    where TRow : class
{
    private int _position = -1;

    public event NotifyCollectionChangedEventHandler? CollectionChanged
    {
        add
        {
            if (rows is INotifyCollectionChanged notifying)
            {
                notifying.CollectionChanged += value;
            }
        }

        remove
        {
            if (rows is INotifyCollectionChanged notifying)
            {
                notifying.CollectionChanged -= value;
            }
        }
    }

    public event EventHandler<DataGridCurrentChangingEventArgs>? CurrentChanging;

    public event EventHandler? CurrentChanged;

    /// <summary>The row list this view reads.</summary>
    public IList Rows => rows;

    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    // This view, not the row list, so a consumer that walks the source is counted too (sprint 2.3.6 review).
    public IEnumerable SourceCollection => this;

    // The grid reads Filter only when CanFilter is true.
    public Func<object, bool> Filter { get => null!; set { } }

    public bool CanFilter => false;

    // The grid raises Sorting first; the grid behaviors handle it and ask the worker. The descriptions only show its order.
    public DataGridSortDescriptionCollection SortDescriptions { get; } = [];

    public bool CanSort => true;

    public bool CanGroup => false;

    public bool IsGrouping => false;

    // The grid reads Groups only when IsGrouping is true.
    public IAvaloniaReadOnlyList<object> Groups => null!;

    public int GroupingDepth => 0;

    public bool IsEmpty => rows.Count == 0;

    // Null before the first or after the last row, as the grid expects (the interface is not annotated for it).
    public object CurrentItem => (_position >= 0 && _position < rows.Count ? rows[_position] : null)!;

    public int CurrentPosition => _position;

    public bool IsCurrentAfterLast => _position >= rows.Count;

    public bool IsCurrentBeforeFirst => _position < 0;

    public int Count => rows.Count;

    public bool IsFixedSize => true;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object? this[int index] { get => rows[index]; set => throw new NotSupportedException(); }

    /// <summary>Shows <paramref name="path"/> as the sorted column ("Bytes|desc" from the view model), or no arrow.</summary>
    public void ShowSort(string? state)
    {
        SortDescriptions.Clear();
        if (state?.Split('|') is [{ Length: > 0 } path, var direction])
        {
            SortDescriptions.Add(DataGridSortDescription.FromPath(path, direction == "desc" ? ListSortDirection.Descending : ListSortDirection.Ascending));
        }
    }

    public string GetGroupingPropertyNameAtDepth(int level) => string.Empty;

    public bool Contains(object? item) => rows.Contains(item);

    public int IndexOf(object? value) => rows.IndexOf(value);

    public IDisposable DeferRefresh() => new Deferral();

    public void Refresh()
    {
    }

    public bool MoveCurrentTo(object? item) => MoveCurrentToPosition(item is null ? -1 : rows.IndexOf(item));

    public bool MoveCurrentToFirst() => MoveCurrentToPosition(0);

    public bool MoveCurrentToLast() => MoveCurrentToPosition(rows.Count - 1);

    public bool MoveCurrentToNext() => MoveCurrentToPosition(_position + 1);

    public bool MoveCurrentToPrevious() => MoveCurrentToPosition(_position - 1);

    public bool MoveCurrentToPosition(int position)
    {
        if (position < -1 || position > rows.Count)
        {
            return false;
        }

        var changing = new DataGridCurrentChangingEventArgs();
        CurrentChanging?.Invoke(this, changing);
        if (changing.Cancel)
        {
            return false;
        }

        _position = position;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return CurrentItem is not null;
    }

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    public void CopyTo(Array array, int index)
    {
        VirtualCollectionViews.Count();
        rows.CopyTo(array, index);
    }

    // Typed, so the grid learns the row type without walking the list (it would otherwise enumerate to find it).
    public IEnumerator<TRow> GetEnumerator()
    {
        VirtualCollectionViews.Count();
        foreach (var row in rows)
        {
            yield return (TRow)row!;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class Deferral : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
