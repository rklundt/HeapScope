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

using HeapScope.Analysis.Inspections;
using HeapScope.Contracts;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// An inspection's rows as a result set (sprint 1.6). The rows read the graph while formatting, so the set holds a
/// snapshot lease as its owner, like the instances source.
/// </summary>
internal sealed class InspectionSource(InspectionTable table) : IResultSource
{
    public IReadOnlyList<ResultColumn> Columns { get; } = [.. table.Columns.Select(c => new ResultColumn(c.Name, KindOf(c.Kind), c.IsKey))];

    public int Count => table.Count;

    public long EstimatedBytes => table.EstimatedBytes;

    public void FormatRow(int index, string?[] cells) => table.FormatRow(index, cells);

    private static ResultColumnKind KindOf(InspectionColumnKind kind) => kind switch
    {
        InspectionColumnKind.Integer => ResultColumnKind.Integer,
        InspectionColumnKind.Bytes => ResultColumnKind.Bytes,
        InspectionColumnKind.Address => ResultColumnKind.Address,
        InspectionColumnKind.Percent => ResultColumnKind.Percent,
        _ => ResultColumnKind.Text,
    };
}
