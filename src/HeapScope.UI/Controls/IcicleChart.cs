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
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using HeapScope.Contracts;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.Controls;

/// <summary>
/// The Retainers tab's icicle chart (sprint 2.4): the focus as the top bar and its dominator children below, as wide as
/// what they retain. A click selects a bar (and the path to it is highlighted); a double-click runs
/// <see cref="FocusCommand"/> with it. One control drawn in one pass; the geometry is <see cref="IcicleGeometry"/>'s.
/// </summary>
public sealed class IcicleChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<IcicleSegmentDto>?> SegmentsProperty =
        AvaloniaProperty.Register<IcicleChart, IReadOnlyList<IcicleSegmentDto>?>(nameof(Segments));

    public static readonly StyledProperty<long> TotalProperty =
        AvaloniaProperty.Register<IcicleChart, long>(nameof(Total));

    public static readonly StyledProperty<IcicleSegmentDto?> SelectedSegmentProperty =
        AvaloniaProperty.Register<IcicleChart, IcicleSegmentDto?>(nameof(SelectedSegment), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<ICommand?> FocusCommandProperty =
        AvaloniaProperty.Register<IcicleChart, ICommand?>(nameof(FocusCommand));

    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<IcicleChart, double>(nameof(RowHeight), 22);

    private IReadOnlyList<IcicleBar> _bars = [];

    static IcicleChart()
    {
        AffectsRender<IcicleChart>(SegmentsProperty, TotalProperty, SelectedSegmentProperty, RowHeightProperty);
        AffectsMeasure<IcicleChart>(SegmentsProperty, RowHeightProperty);
    }

    public IcicleChart()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public IReadOnlyList<IcicleSegmentDto>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>The top bar's bytes: the chart's full width.</summary>
    public long Total
    {
        get => GetValue(TotalProperty);
        set => SetValue(TotalProperty, value);
    }

    public IcicleSegmentDto? SelectedSegment
    {
        get => GetValue(SelectedSegmentProperty);
        set => SetValue(SelectedSegmentProperty, value);
    }

    /// <summary>Run with the double-clicked segment.</summary>
    public ICommand? FocusCommand
    {
        get => GetValue(FocusCommandProperty);
        set => SetValue(FocusCommandProperty, value);
    }

    public double RowHeight
    {
        get => GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, IcicleGeometry.Height(Segments, RowHeight));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        if (IcicleGeometry.HitTest(_bars, point.X, point.Y) is not { } segment)
        {
            return;
        }

        SelectedSegment = segment;
        if (e.ClickCount == 2 && FocusCommand is { } command && command.CanExecute(segment))
        {
            command.Execute(segment);
        }

        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var width = Bounds.Width;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        _bars = Segments is { } segments ? IcicleGeometry.Bars(segments, Total, width, RowHeight) : [];
        if (_bars.Count == 0)
        {
            return;
        }

        var bars = new[] { Brush("Icicle.Bar1"), Brush("Icicle.Bar2") };
        var path = Brush("Icicle.Path") ?? Brushes.Orange;
        var text = Brush("Brush.Text") ?? Brushes.Black;
        var border = new Pen(Brush("Brush.Surface") ?? Brushes.White, 1);
        var selected = SelectedSegment;
        foreach (var bar in _bars)
        {
            var rect = new Rect(bar.X, bar.Y, bar.Width, bar.Height - 1);
            var fill = IcicleGeometry.IsOnPath(bar.Segment, selected) ? path : bars[bar.Segment.Level % 2] ?? Brushes.LightBlue;
            context.DrawRectangle(fill, border, rect);
            if (bar.Width >= 40)
            {
                var label = new FormattedText(
                    string.Create(CultureInfo.CurrentCulture, $"{bar.Segment.Name} ({Format.Bytes(bar.Segment.Width)})"),
                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, text)
                {
                    MaxTextWidth = Math.Max(1, bar.Width - 6),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                context.DrawText(label, new Point(bar.X + 3, bar.Y + ((bar.Height - label.Height) / 2)));
            }
        }
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
}
