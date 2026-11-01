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

namespace HeapScope.Application.Presentation;

/// <summary>Invariant-culture cell parsing; a missing column (index -1) or empty cell reads as zero or empty.</summary>
public static class Cells
{
    public static string Text(IReadOnlyList<string?> cells, int index)
    {
        ArgumentNullException.ThrowIfNull(cells);
        return index >= 0 && index < cells.Count ? cells[index] ?? string.Empty : string.Empty;
    }

    public static long Long(IReadOnlyList<string?> cells, int index)
    {
        ArgumentNullException.ThrowIfNull(cells);
        return index >= 0 && index < cells.Count && long.TryParse(cells[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>An address cell (<c>0x1f2a</c>) as a number; 0 when it is not one.</summary>
    public static ulong Address(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    public static double Double(IReadOnlyList<string?> cells, int index)
    {
        ArgumentNullException.ThrowIfNull(cells);
        return index >= 0 && index < cells.Count && double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
