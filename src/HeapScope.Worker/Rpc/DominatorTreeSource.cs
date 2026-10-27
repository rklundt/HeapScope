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

using System.Globalization;
using HeapScope.Analysis.Retention;
using HeapScope.Contracts;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// A flattened dominator tree as a result set (sprint 2.4): one row per tree row, already in display order, with each object
/// row's address (0 for the other rows), so the set holds nothing per object of the snapshot.
/// </summary>
internal sealed class DominatorTreeSource(DominatorTreeView view, ulong[] rowAddresses) : IResultSource
{
    public static readonly ResultColumn[] TreeColumns =
    [
        new(DominatorTreeColumns.Key, ResultColumnKind.Text, IsKey: true, IsHidden: true),
        new(DominatorTreeColumns.ParentKey, ResultColumnKind.Text, IsHidden: true),
        new(DominatorTreeColumns.Depth, ResultColumnKind.Integer),
        new(DominatorTreeColumns.Kind, ResultColumnKind.Text),
        new(DominatorTreeColumns.Name, ResultColumnKind.Text),
        new(DominatorTreeColumns.Module, ResultColumnKind.Text),
        new(DominatorTreeColumns.Address, ResultColumnKind.Address),
        new(DominatorTreeColumns.Objects, ResultColumnKind.Integer),
        new(DominatorTreeColumns.Retained, ResultColumnKind.Bytes),
        new(DominatorTreeColumns.Bytes, ResultColumnKind.Bytes),
        new(DominatorTreeColumns.Percent, ResultColumnKind.Percent),
        new(DominatorTreeColumns.Children, ResultColumnKind.Integer, IsHidden: true),
        new(DominatorTreeColumns.Expanded, ResultColumnKind.Integer, IsHidden: true),
    ];

    public IReadOnlyList<ResultColumn> Columns => TreeColumns;

    public int Count => view.Rows.Count;

    public long EstimatedBytes => view.Rows.Count * 248L;

    public void FormatRow(int index, string?[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var row = view.Rows[index];
        cells[0] = row.Key;
        cells[1] = row.ParentKey;
        cells[2] = row.Depth.ToString(CultureInfo.InvariantCulture);
        cells[3] = row.Kind switch
        {
            DominatorRowKind.Group => DominatorRowKinds.Group,
            DominatorRowKind.More => DominatorRowKinds.More,
            _ => DominatorRowKinds.Object,
        };
        cells[4] = row.Name;
        cells[5] = row.Module;
        cells[6] = row.Object >= 0 ? "0x" + rowAddresses[index].ToString("x", CultureInfo.InvariantCulture) : null;
        cells[7] = row.Objects.ToString(CultureInfo.InvariantCulture);
        cells[8] = row.Retained.ToString(CultureInfo.InvariantCulture);
        cells[9] = row.Bytes.ToString(CultureInfo.InvariantCulture);
        cells[10] = view.TotalRetained == 0 ? "0" : (100.0 * row.Retained / view.TotalRetained).ToString("0.##", CultureInfo.InvariantCulture);
        cells[11] = row.Children.ToString(CultureInfo.InvariantCulture);
        cells[12] = row.Expanded ? "1" : "0";
    }
}
