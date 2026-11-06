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

namespace HeapScope.UI.Tests;

/// <summary>WCAG 2.x contrast of two opaque theme colors, written #AARRGGBB.</summary>
internal static class Contrast
{
    public static bool IsOpaque(string color) => Parse(color).A == 255;

    public static double Ratio(string a, string b)
    {
        // A translucent color's contrast depends on what is under it, so a pair is only checked opaque.
        IsOpaque(a).ShouldBeTrue($"{a} is not opaque");
        IsOpaque(b).ShouldBeTrue($"{b} is not opaque");
        var (la, lb) = (Luminance(Parse(a)), Luminance(Parse(b)));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static (byte A, byte R, byte G, byte B) Parse(string color)
    {
        color.Length.ShouldBe(9);
        var value = uint.Parse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private static double Luminance((byte A, byte R, byte G, byte B) c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Channel(byte value)
    {
        var s = value / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
