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
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace HeapScope.UI.Controls;

/// <summary>
/// A background named by a data row (a heap kind's color, a bookmark's mark): <c>ctl:ThemeBrush.Background="{Binding BrushKey}"</c>.
/// It follows the resource, so it changes with the theme, which a converter resolving it once would not.
/// </summary>
public static class ThemeBrush
{
    public static readonly AttachedProperty<string?> BackgroundProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Background", typeof(ThemeBrush));

    private static readonly AttachedProperty<IDisposable?> s_subscription =
        AvaloniaProperty.RegisterAttached<Control, IDisposable?>("Subscription", typeof(ThemeBrush));

    static ThemeBrush()
    {
        BackgroundProperty.Changed.AddClassHandler<Control>(static (control, change) =>
        {
            control.GetValue(s_subscription)?.Dispose();
            var property = control switch
            {
                Border => Border.BackgroundProperty,
                TemplatedControl => TemplatedControl.BackgroundProperty,
                _ => throw new ArgumentException("ThemeBrush.Background works on a Border or a templated control.", nameof(control)),
            };
            control.SetValue(s_subscription, change.GetNewValue<string?>() is { } key
                ? control.Bind(property, control.GetResourceObservable(key, static value => value as IBrush))
                : null);
        });
    }

    public static string? GetBackground(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(BackgroundProperty);
    }

    public static void SetBackground(Control control, string? value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(BackgroundProperty, value);
    }
}
