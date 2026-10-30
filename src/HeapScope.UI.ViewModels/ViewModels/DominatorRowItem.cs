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
using CommunityToolkit.Mvvm.ComponentModel;
using HeapScope.Application.Presentation;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;

/// <summary>A row of the dominator tree grid (sprint 2.4), filled from the worker's columns by name.</summary>
public sealed class DominatorRowItem(int index, bool typeGroups) : ObservableObject, IIndexedRow
{
    /// <summary>Pixels of indentation per tree level.</summary>
    public const double IndentPerLevel = 16;

    public int Index { get; } = index;

    public bool IsLoaded { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string ParentKey { get; private set; } = string.Empty;

    public int Depth { get; private set; }

    /// <summary>One of <see cref="DominatorRowKinds"/>.</summary>
    public string Kind { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Module { get; private set; }

    public ulong Address { get; private set; }

    public string AddressText => Address == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"0x{Address:x}");

    public long Objects { get; private set; }

    public long Retained { get; private set; }

    public long Bytes { get; private set; }

    public double Percent { get; private set; }

    public int Children { get; private set; }

    public bool IsExpanded { get; private set; }

    public bool IsObject => Kind == DominatorRowKinds.Object;

    /// <summary>
    /// The type an object row or a type group stands for; none for a namespace group or a "more" row. Whether groups are
    /// types comes from the grouping the rows were asked with (a type without a module has no module either).
    /// </summary>
    public string? TypeName => IsObject || (Kind == DominatorRowKinds.Group && typeGroups) ? Name : null;

    /// <summary>A "more" row or a node with children expands.</summary>
    public bool CanExpand => Kind == DominatorRowKinds.More || Children > 0;

    public double Indent => Depth * IndentPerLevel;

    /// <summary>The arrow: open, closed, or none for a leaf.</summary>
    public string Glyph => !CanExpand ? string.Empty : IsExpanded ? "▾" : "▸";

    public static VirtualRowList<DominatorRowItem> CreateList(ResultSetHandle? rows, ResultSetInfo info, bool typeGroups, Action<Exception>? onError = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        var at = info.Columns.Select((c, i) => (c.Name, i)).ToDictionary(c => c.Name, c => c.i, StringComparer.Ordinal);
        if (rows is not null)
        {
            foreach (var column in new[] { DominatorTreeColumns.Key, DominatorTreeColumns.Kind, DominatorTreeColumns.Retained })
            {
                ResultColumns.Require(info.Columns, column);
            }
        }

        return new VirtualRowList<DominatorRowItem>(rows, info.TotalCount, i => new DominatorRowItem(i, typeGroups), (row, cells) => row.Fill(cells, at), onError, logger);
    }

    private void Fill(IReadOnlyList<string?> cells, Dictionary<string, int> at)
    {
        string? Cell(string column) => at.TryGetValue(column, out var i) && i < cells.Count ? cells[i] : null;
        long Number(string column) => long.TryParse(Cell(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        Key = Cell(DominatorTreeColumns.Key) ?? string.Empty;
        ParentKey = Cell(DominatorTreeColumns.ParentKey) ?? string.Empty;
        Depth = (int)Number(DominatorTreeColumns.Depth);
        Kind = Cell(DominatorTreeColumns.Kind) ?? string.Empty;
        Name = Cell(DominatorTreeColumns.Name) ?? string.Empty;
        Module = Cell(DominatorTreeColumns.Module);
        Address = Cell(DominatorTreeColumns.Address) is { Length: > 2 } address
            && ulong.TryParse(address.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) ? a : 0;
        Objects = Number(DominatorTreeColumns.Objects);
        Retained = Number(DominatorTreeColumns.Retained);
        Bytes = Number(DominatorTreeColumns.Bytes);
        Percent = double.TryParse(Cell(DominatorTreeColumns.Percent), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
        Children = (int)Number(DominatorTreeColumns.Children);
        IsExpanded = Cell(DominatorTreeColumns.Expanded) == "1";
        IsLoaded = true;
        OnPropertyChanged(string.Empty);
    }
}
