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
using Avalonia.Controls.Documents;
using Avalonia.Media;
using HeapScope.Contracts;

namespace HeapScope.UI.Controls;

/// <summary>Shows a name with the characters matched by the type filter in bold (dotMemory's match highlighting).</summary>
public static class HighlightedText
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(HighlightedText));

    public static readonly AttachedProperty<string?> FilterProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Filter", typeof(HighlightedText));

    static HighlightedText()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((block, _) => Update(block));
        FilterProperty.Changed.AddClassHandler<TextBlock>((block, _) => Update(block));
    }

    public static string? GetText(TextBlock block) => block?.GetValue(TextProperty);

    public static void SetText(TextBlock block, string? value) => block?.SetValue(TextProperty, value);

    public static string? GetFilter(TextBlock block) => block?.GetValue(FilterProperty);

    public static void SetFilter(TextBlock block, string? value) => block?.SetValue(FilterProperty, value);

    private static void Update(TextBlock block)
    {
        var text = GetText(block) ?? string.Empty;
        var ranges = NameFilter.Match(text, GetFilter(block));
        if (ranges is not { Count: > 0 })
        {
            // Plain text, without building inlines, for the cells without a match (most of them).
            block.Inlines = null;
            block.Text = text;
            return;
        }

        var inlines = new InlineCollection();
        var position = 0;
        foreach (var (start, length) in ranges)
        {
            if (start > position)
            {
                inlines.Add(new Run(text[position..start]));
            }

            inlines.Add(new Run(text.Substring(start, length)) { FontWeight = FontWeight.Bold });
            position = start + length;
        }

        if (position < text.Length)
        {
            inlines.Add(new Run(text[position..]));
        }

        block.Inlines = inlines;
    }
}
