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

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using HeapScope.Application.Monitoring;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.Wpf.Controls;

/// <summary>
/// The Live page's chart (sprint 1.7): the GC heap stacked by heap kind as of the last GC (gen0, gen1, gen2, LOH, POH, in
/// the fixed heap kind colors) and, on top, what was allocated since, over the samples' time span, with a vertical line
/// at each snapshot. One element drawn once per change
/// of <see cref="Version"/> (frozen geometry, no per-point visuals), so a 30-minute window stays cheap to redraw.
/// </summary>
public sealed class TimelineChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IReadOnlyList<HeapSample>), typeof(TimelineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(
        nameof(Markers), typeof(IReadOnlyList<DateTimeOffset>), typeof(TimelineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(TimelineChart), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<HeapSample>? Samples
    {
        get => (IReadOnlyList<HeapSample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public IReadOnlyList<DateTimeOffset>? Markers
    {
        get => (IReadOnlyList<DateTimeOffset>?)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    /// <summary>Changes when the samples changed (the list itself is the same object).</summary>
    public int Version
    {
        get => (int)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var (width, height) = (ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (Samples is not { Count: > 0 } samples || width <= 0 || height <= 0)
        {
            return;
        }

        var layers = TimelineLayout.Layers(samples, width, height, out var maxBytes);
        for (var k = layers.Count - 1; k >= 0; k--)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(layers[k][0].X, height), isFilled: true, isClosed: true);
                context.PolyLineTo([.. layers[k].Select(p => new Point(p.X, p.Y))], isStroked: false, isSmoothJoin: false);
                context.LineTo(new Point(layers[k][^1].X, height), isStroked: false, isSmoothJoin: false);
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(TryFindResource(TimelineLayout.LayerKeys[k]) as Brush ?? Brushes.Gray, null, geometry);
        }

        var text = TryFindResource("Brush.Text") as Brush ?? Brushes.Black;
        var marker = new Pen(text, 1) { DashStyle = DashStyles.Dash };
        marker.Freeze();
        foreach (var at in Markers ?? [])
        {
            if (TimelineLayout.XOf(samples, at, width) is { } x)
            {
                drawingContext.DrawLine(marker, new Point(x, 0), new Point(x, height));
            }
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var label = new FormattedText(
            string.Create(CultureInfo.CurrentCulture, $"{maxBytes / (1024.0 * 1024.0):N0} MB"),
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, text, dpi);
        drawingContext.DrawText(label, new Point(4, 2));
    }
}
