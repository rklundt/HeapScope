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

namespace HeapScope.Spikes.AvaloniaGrid;

/// <summary>Forwards to a row list, counting how the grid reads it (which calls touch every row).</summary>
internal sealed class RowListProbe(IList inner) : IList, INotifyCollectionChanged
{
    public static int Enumerations;
    public static long EnumeratedItems;
    public static int CopyTos;
    public static long IndexerReads;

    public event NotifyCollectionChangedEventHandler? CollectionChanged
    {
        add => ((INotifyCollectionChanged)inner).CollectionChanged += value;
        remove => ((INotifyCollectionChanged)inner).CollectionChanged -= value;
    }

    public object? this[int index] { get { IndexerReads++; return inner[index]; } set => throw new NotSupportedException(); }
    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public int Count => inner.Count;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(object? value) => inner.Contains(value);
    public int IndexOf(object? value) => inner.IndexOf(value);
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(Array array, int index) { CopyTos++; inner.CopyTo(array, index); }
    public IEnumerator GetEnumerator()
    {
        Enumerations++;
        Console.Error.WriteLine("ENUMERATED: " + Environment.StackTrace);
        foreach (var item in inner) { EnumeratedItems++; yield return item; }
    }
}
