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
using Avalonia.Controls;

namespace HeapScope.UI.Controls;

/// <summary>
/// A status line or note that takes no space when it has no text (sprint 2.4, 2.3.7 review M14): every grid's status,
/// explanation and retained note was a text block with the same visibility binding repeated.
/// </summary>
public sealed class NoteText : TextBlock
{
    static NoteText()
    {
        TextProperty.Changed.AddClassHandler<NoteText>((note, _) => note.IsVisible = !string.IsNullOrEmpty(note.Text));
    }

    public NoteText()
    {
        IsVisible = false;
    }

    // Styled as a text block (the subtle class and the theme's text styles).
    protected override Type StyleKeyOverride => typeof(TextBlock);
}
