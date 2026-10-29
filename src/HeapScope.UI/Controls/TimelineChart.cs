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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HeapScope.Application.Monitoring;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.Controls;

/// <summary>
/// The Live page's chart (sprint 1.7): the GC heap stacked by heap kind as of the last GC (gen0, gen1, gen2, LOH, POH, in
/// the fixed heap kind colors) and, on top, what was allocated since, over the samples' time span, with a dashed line at
/// each snapshot. One control drawn once per change of <see cref="Version"/> (one geometry per layer, no per-point
/// visuals), so a 30-minute window stays cheap to redraw. The geometry is <see cref="TimelineLayout"/>'s.
/// </summary>
public sealed class TimelineChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<HeapSample>?> SamplesProperty =
        AvaloniaProperty.Register<TimelineChart, IReadOnlyList<HeapSample>?>(nameof(Samples));

    public static readonly StyledProperty<IReadOnlyList<DateTimeOffset>?> MarkersProperty =
        AvaloniaProperty.Register<TimelineChart, IReadOnlyList<DateTimeOffset>?>(nameof(Markers));

    /// <summary>When the target was made to collect (sprint 3.4), drawn as dotted lines.</summary>
    public static readonly StyledProperty<IReadOnlyList<DateTimeOffset>?> GcMarkersProperty =
        AvaloniaProperty.Register<TimelineChart, IReadOnlyList<DateTimeOffset>?>(nameof(GcMarkers));

    public static readonly StyledProperty<int> VersionProperty =
        AvaloniaProperty.Register<TimelineChart, int>(nameof(Version));

    static TimelineChart()
    {
        AffectsRender<TimelineChart>(SamplesProperty, MarkersProperty, GcMarkersProperty, VersionProperty);
    }

    public IReadOnlyList<HeapSample>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public IReadOnlyList<DateTimeOffset>? GcMarkers
    {
        get => GetValue(GcMarkersProperty);
        set => SetValue(GcMarkersProperty, value);
    }

    public IReadOnlyList<DateTimeOffset>? Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    /// <summary>Changes when the samples changed (the list itself is the same object).</summary>
    public int Version
    {
        get => GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (width, height) = (Bounds.Width, Bounds.Height);
        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, width, height));
        if (Samples is not { Count: > 0 } samples || width <= 0 || height <= 0)
        {
            return;
        }

        var layers = TimelineLayout.Layers(samples, width, height, out var maxBytes);
        for (var k = layers.Count - 1; k >= 0; k--)
        {
            var geometry = new StreamGeometry();
            using (var figure = geometry.Open())
            {
                figure.BeginFigure(new Point(layers[k][0].X, height), isFilled: true);
                foreach (var point in layers[k])
                {
                    figure.LineTo(new Point(point.X, point.Y));
                }

                figure.LineTo(new Point(layers[k][^1].X, height));
                figure.EndFigure(isClosed: true);
            }

            context.DrawGeometry(Brush(TimelineLayout.LayerKeys[k]) ?? Brushes.Gray, null, geometry);
        }

        var text = Brush("Brush.Text") ?? Brushes.Black;
        var marker = new Pen(text, 1, DashStyle.Dash);
        foreach (var at in Markers ?? [])
        {
            if (TimelineLayout.XOf(samples, at, width) is { } x)
            {
                context.DrawLine(marker, new Point(x, 0), new Point(x, height));
            }
        }

        var gc = new Pen(Brush("Brush.Growth") ?? Brushes.OrangeRed, 1, DashStyle.Dot);
        foreach (var at in GcMarkers ?? [])
        {
            if (TimelineLayout.XOf(samples, at, width) is { } x)
            {
                context.DrawLine(gc, new Point(x, 0), new Point(x, height));
            }
        }

        var label = new FormattedText(
            string.Create(CultureInfo.CurrentCulture, $"{maxBytes / (1024.0 * 1024.0):N0} MB"),
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, text);
        context.DrawText(label, new Point(4, 2));
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
}
