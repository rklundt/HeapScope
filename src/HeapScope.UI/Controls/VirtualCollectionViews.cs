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
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.Controls;

/// <summary>Creates <see cref="VirtualCollectionView{TRow}"/>s for row lists, and counts whole-list enumerations.</summary>
public static class VirtualCollectionViews
{
    private static long s_enumerations;

    /// <summary>Whole-list enumerations and copies of any view so far.</summary>
    public static long Enumerations => Interlocked.Read(ref s_enumerations);

    /// <summary>
    /// The view for <paramref name="rows"/>: a pass-through view for a <see cref="VirtualRowList{TRow}"/>; any other list
    /// (small lists of a few rows) as it is.
    /// </summary>
    public static IEnumerable? For(IList? rows)
    {
        if (rows?.GetType() is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(VirtualRowList<>))
        {
            var view = typeof(VirtualCollectionView<>).MakeGenericType(type.GetGenericArguments()[0]);
            return (IEnumerable)Activator.CreateInstance(view, rows)!;
        }

        return rows;
    }

    internal static void Count() => Interlocked.Increment(ref s_enumerations);
}
