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

namespace HeapScope.Worker.Rpc;

/// <summary>
/// The rows of a result set, kept as compact typed data and formatted to string cells only for the page a client
/// asks for (ARCHITECTURE.md 4.6). Never one string per cell for the whole set.
/// </summary>
internal interface IResultSource
{
    IReadOnlyList<ResultColumn> Columns { get; }

    int Count { get; }

    /// <summary>Approximate bytes held by this source, used by the store's byte budget.</summary>
    long EstimatedBytes { get; }

    /// <summary>Writes the cells of row <paramref name="index"/> into <paramref name="cells"/> (one per column).</summary>
    void FormatRow(int index, string?[] cells);
}
