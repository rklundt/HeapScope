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
using Avalonia.Controls.Templates;
using Avalonia.Layout;

namespace HeapScope.UI.Controls;

/// <summary>
/// Puts an icon before a button's text (<c>ctl:ButtonIcon.Icon="OpenFile"</c>). The button's content stays its text, so
/// screen readers and tests read the same words as before.
/// </summary>
public static class ButtonIcon
{
    public static readonly AttachedProperty<string?> IconProperty =
        AvaloniaProperty.RegisterAttached<Button, string?>("Icon", typeof(ButtonIcon));

    static ButtonIcon()
    {
        IconProperty.Changed.AddClassHandler<Button>(static (button, change) =>
        {
            var icon = change.GetNewValue<string?>();
            button.ContentTemplate = icon is null ? null : new FuncDataTemplate<object?>((content, _) => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new DuotoneIcon { Icon = icon, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = content?.ToString(), VerticalAlignment = VerticalAlignment.Center },
                },
            });
        });
    }

    public static string? GetIcon(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        return button.GetValue(IconProperty);
    }

    public static void SetIcon(Button button, string? value)
    {
        ArgumentNullException.ThrowIfNull(button);
        button.SetValue(IconProperty, value);
    }
}
