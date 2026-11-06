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


using Avalonia.Data.Converters;

namespace HeapScope.UI.Controls;

/// <summary>The sign of a delta, for the style classes that color it (<c>growth</c>, <c>shrink</c>): zero has neither.</summary>
public static class SignConverters
{
    public static FuncValueConverter<long, bool> IsPositive { get; } = new(static value => value > 0);

    public static FuncValueConverter<long, bool> IsNegative { get; } = new(static value => value < 0);

    /// <summary>Whether a list has anything in it (its <c>Count</c>), for a panel with nothing to show otherwise.</summary>
    public static FuncValueConverter<int, bool> HasItems { get; } = new(static count => count > 0);
}
