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


using Avalonia.Styling;

namespace HeapScope.UI.Services;

/// <summary>
/// The light or dark theme (sprint 1.3): the system's choice by default (Avalonia follows Windows, macOS and the Linux
/// desktop's setting), or <c>Ui:Theme</c> = Light or Dark from configuration.
/// </summary>
public static class ThemeChooser
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    /// <summary>The theme variant to request: the configured one when valid, otherwise the system's.</summary>
    public static ThemeVariant Choose(string? configured) =>
        string.Equals(configured, Light, StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Light
        : string.Equals(configured, Dark, StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Dark
        : ThemeVariant.Default;
}
