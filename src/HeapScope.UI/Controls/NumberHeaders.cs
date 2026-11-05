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


using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace HeapScope.UI.Controls;

/// <summary>
/// Right-aligns the header of a number column (<c>CellStyleClasses="number"</c>), so it lines up with its numbers (ADR-040).
/// A column has no header style of its own, so this finds the header cells, which exist once the grid has loaded. Every grid
/// names an export (a test checks it), so <see cref="GridExports"/> applies this to all of them when they load.
/// </summary>
internal static class NumberHeaders
{
    public static void Apply(DataGrid grid)
    {
        var numbers = grid.Columns.Where(static c => c.CellStyleClasses.Contains("number") && c.Header is not null).Select(static c => c.Header).ToHashSet();
        foreach (var header in grid.GetVisualDescendants().OfType<DataGridColumnHeader>())
        {
            header.HorizontalContentAlignment = header.Content is { } content && numbers.Contains(content) ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
    }

    /// <summary>For columns built after the grid loaded: their header cells appear in the next layout.</summary>
    public static void ApplyAfterLayout(DataGrid grid) => Dispatcher.UIThread.Post(() => Apply(grid), DispatcherPriority.Loaded);
}
