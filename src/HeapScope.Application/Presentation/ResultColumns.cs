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

using HeapScope.Contracts;

namespace HeapScope.Application.Presentation;

/// <summary>Finds result columns by name; a missing required column means the worker and the front end differ in build.</summary>
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
