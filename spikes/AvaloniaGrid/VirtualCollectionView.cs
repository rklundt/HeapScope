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
using System.Globalization;
using Avalonia.Collections;

namespace HeapScope.Spikes.AvaloniaGrid;

/// <summary>
/// A collection view the DataGrid uses as it is (it wraps any other source in a DataGridCollectionView, which copies every
/// row into a list of its own): reads go to the data-virtualized row list by index, so only the rows on screen exist.
/// Sorting and filtering happen in the worker, through the view model; this view never sorts, filters or groups.
/// </summary>
internal sealed class VirtualCollectionView<TRow>(IList rows) : IDataGridCollectionView, IList, IEnumerable<TRow>
    where TRow : class
{
    private int _position = -1;

    public event NotifyCollectionChangedEventHandler? CollectionChanged
    {
        add => ((INotifyCollectionChanged)rows).CollectionChanged += value;
        remove => ((INotifyCollectionChanged)rows).CollectionChanged -= value;
    }

    public event EventHandler<DataGridCurrentChangingEventArgs>? CurrentChanging;

    public event EventHandler? CurrentChanged;

    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    public IEnumerable SourceCollection => rows;

    public Func<object, bool> Filter { get => null!; set { } }

    public bool CanFilter => false;

    // The grid raises Sorting before it would sort; the window handles it and asks the worker. Nothing is ever added here.
    public DataGridSortDescriptionCollection SortDescriptions { get; } = [];

    public bool CanSort => true;

    public bool CanGroup => false;

    public bool IsGrouping => false;

    public IAvaloniaReadOnlyList<object> Groups => null!;

    public int GroupingDepth => 0;

    public string GetGroupingPropertyNameAtDepth(int level) => string.Empty;

    public bool IsEmpty => rows.Count == 0;

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

    public void CopyTo(Array array, int index) => rows.CopyTo(array, index);

    // Typed, so the grid learns the row type without walking the list; walking it still never fetches (VirtualRowList).
    public IEnumerator<TRow> GetEnumerator()
    {
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
