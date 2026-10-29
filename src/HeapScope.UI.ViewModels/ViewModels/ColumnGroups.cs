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

namespace HeapScope.UI.ViewModels;

/// <summary>
/// Optional column groups of a grid (sprint 2.4, 2.3.7 review M13): a view tags each optional column with its group's
/// name, and the view model says which groups are shown, so a new optional column needs no new grid behavior.
/// </summary>
public static class ColumnGroups
{
    /// <summary>Retained bytes (sprint 2.3).</summary>
    public const string Retained = "Retained";

    /// <summary>Bytes per generation (sprint 1.3).</summary>
    public const string Generations = "Generations";

    /// <summary>New and dead objects by address (sprint 3.3).</summary>
    public const string Identity = "Identity";

    /// <summary>The groups whose flag is set, in a list a view binds.</summary>
    public static IReadOnlyList<string> Of(params (string Group, bool Shown)[] groups) =>
        [.. (groups ?? []).Where(g => g.Shown).Select(g => g.Group)];
}
