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


using Avalonia;
using Avalonia.Media;

namespace HeapScope.UI.Services;

/// <summary>The app's font (ADR-040): Inter is the default family, so charts, popups and tool tips use it too.</summary>
internal static class AppFonts
{
    public const string Inter = "fonts:Inter#Inter";

    public static AppBuilder UseAppFont(this AppBuilder builder) =>
        builder.WithInterFont().With(new FontManagerOptions { DefaultFamilyName = Inter });
}
