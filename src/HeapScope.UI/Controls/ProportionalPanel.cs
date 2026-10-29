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
using Avalonia.Controls.Presenters;

namespace HeapScope.UI.Controls;

/// <summary>Lays children out side by side, each as wide as its share of the whole (the heap kinds bar).</summary>
public sealed class ProportionalPanel : Panel
{
    public static readonly AttachedProperty<double> WeightProperty =
        AvaloniaProperty.RegisterAttached<ProportionalPanel, Control, double>("Weight");

    static ProportionalPanel()
    {
        AffectsParentArrange<ProportionalPanel>(WeightProperty);
    }

    public static double GetWeight(Control element) => element?.GetValue(WeightProperty) ?? 0;

    public static void SetWeight(Control element, double value) => element?.SetValue(WeightProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var total = Children.Sum(c => Math.Max(0, GetWeight(Container(c))));
        var x = 0.0;
        foreach (var child in Children)
        {
            var width = total <= 0 ? 0 : finalSize.Width * Math.Max(0, GetWeight(Container(child))) / total;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }

        return finalSize;
    }

    /// <summary>Items controls wrap items in presenters; the weight is set on the item's root element.</summary>
    private static Control Container(Control child) => child is ContentPresenter { Child: { } root } ? root : child;
}
