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
using Avalonia.Data.Converters;

namespace HeapScope.UI.Controls;

/// <summary>A diff delta's color (sprint 1.4): growth for a positive trend, shrinkage for a negative one, the text color otherwise.</summary>
public sealed class TrendBrushConverter : IValueConverter
{
    public static TrendBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        > 0 => ResourceKeyToBrushConverter.Instance.Convert("Brush.Growth", targetType, null, culture),
        < 0 => ResourceKeyToBrushConverter.Instance.Convert("Brush.Shrink", targetType, null, culture),
        _ => ResourceKeyToBrushConverter.Instance.Convert("Brush.Text", targetType, null, culture),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
