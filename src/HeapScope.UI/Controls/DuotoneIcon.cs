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

namespace HeapScope.UI.Controls;

/// <summary>
/// One of HeapScope's icons (<c>Themes/Icons.axaml</c>, ADR-040): an outline in the surrounding text color over a tint of the
/// accent, drawn on a 20 px grid and scaled to the control's size. Redraws when the theme changes.
/// </summary>
public sealed class DuotoneIcon : Control
{
    private const double Grid = 20;
    private const double Stroke = 1.5;
    private const double TintOpacity = 0.28;

    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<DuotoneIcon, string?>(nameof(Icon));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<DuotoneIcon>();

    static DuotoneIcon()
    {
        AffectsRender<DuotoneIcon>(IconProperty, ForegroundProperty);
    }

    public DuotoneIcon()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>The icon's name: <c>Icon.Name.Line</c> and, if there is one, <c>Icon.Name.Tint</c> in the resources.</summary>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Icon is not { } name || Find<Geometry>($"Icon.{name}.Line") is not { } line)
        {
            return;
        }

        var scale = Math.Min(Bounds.Width, Bounds.Height) / Grid;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            if (Find<Geometry>($"Icon.{name}.Tint") is { } tint && Find<IBrush>("Brush.Accent") is { } accent)
            {
                using (context.PushOpacity(TintOpacity))
                {
                    context.DrawGeometry(accent, null, tint);
                }
            }

            context.DrawGeometry(null, new Pen(Foreground ?? Brushes.Gray, Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    private T? Find<T>(string key)
        where T : class =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as T : null;
}
