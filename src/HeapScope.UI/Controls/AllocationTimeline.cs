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
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using HeapScope.Contracts;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.Controls;

/// <summary>
/// The allocations timeline (sprint 2.5): sampled bytes per stretch of the trace as bars; dragging selects an interval
/// (<see cref="Selection"/>), a click without a drag selects the whole trace again. The geometry is
/// <see cref="AllocationTimelineGeometry"/>'s.
/// </summary>
public sealed class AllocationTimeline : Control
{
    public static readonly StyledProperty<IReadOnlyList<AllocationBucketDto>?> BucketsProperty =
        AvaloniaProperty.Register<AllocationTimeline, IReadOnlyList<AllocationBucketDto>?>(nameof(Buckets));

    public static readonly StyledProperty<double> BucketSecondsProperty =
        AvaloniaProperty.Register<AllocationTimeline, double>(nameof(BucketSeconds), 1);

    public static readonly StyledProperty<double> DurationSecondsProperty =
        AvaloniaProperty.Register<AllocationTimeline, double>(nameof(DurationSeconds));

    public static readonly StyledProperty<TimeInterval?> SelectionProperty =
        AvaloniaProperty.Register<AllocationTimeline, TimeInterval?>(nameof(Selection), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>A drag shorter than this many pixels is a click.</summary>
    private const double MinDrag = 3;

    private double? _dragStart;
    private double _dragNow;

    static AllocationTimeline()
    {
        AffectsRender<AllocationTimeline>(BucketsProperty, BucketSecondsProperty, DurationSecondsProperty, SelectionProperty);
    }

    public AllocationTimeline()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public IReadOnlyList<AllocationBucketDto>? Buckets
    {
        get => GetValue(BucketsProperty);
        set => SetValue(BucketsProperty, value);
    }

    public double BucketSeconds
    {
        get => GetValue(BucketSecondsProperty);
        set => SetValue(BucketSecondsProperty, value);
    }

    public double DurationSeconds
    {
        get => GetValue(DurationSecondsProperty);
        set => SetValue(DurationSecondsProperty, value);
    }

    /// <summary>The selected interval, or null for the whole trace.</summary>
    public TimeInterval? Selection
    {
        get => GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragStart = _dragNow = Math.Clamp(e.GetPosition(this).X, 0, Bounds.Width);
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        if (_dragStart is not null)
        {
            _dragNow = Math.Clamp(e.GetPosition(this).X, 0, Bounds.Width);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (_dragStart is not { } start)
        {
            return;
        }

        _dragStart = null;
        e.Pointer.Capture(null);
        var end = Math.Clamp(e.GetPosition(this).X, 0, Bounds.Width);
        Selection = Math.Abs(end - start) < MinDrag
            ? null
            : new TimeInterval(SecondsAt(start), SecondsAt(end));
        e.Handled = true;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var size = Bounds.Size;
        context.FillRectangle(Brushes.Transparent, new Rect(size));
        var bars = AllocationTimelineGeometry.Bars(Buckets, BucketSeconds, DurationSeconds, size.Width, size.Height - 14);
        var barBrush = Brush("Allocation.Bar") ?? Brushes.SteelBlue;
        foreach (var bar in bars)
        {
            context.FillRectangle(barBrush, new Rect(bar.X, bar.Y, bar.Width, bar.Height));
        }

        // The selected interval, or the one being dragged.
        var interval = Brush("Allocation.Interval") ?? new SolidColorBrush(Color.FromArgb(0x55, 0x48, 0xB8, 0x60));
        if (_dragStart is { } start)
        {
            context.FillRectangle(interval, new Rect(Math.Min(start, _dragNow), 0, Math.Abs(_dragNow - start), size.Height));
        }
        else if (Selection is { } selection)
        {
            var from = XOf(selection.FromSeconds);
            context.FillRectangle(interval, new Rect(from, 0, Math.Max(1, XOf(selection.ToSeconds) - from), size.Height));
        }

        // The time scale: the start and the end.
        var text = Brush("Brush.Subtle") ?? Brush("Brush.Text") ?? Brushes.Gray;
        var startLabel = Label("0 s", text);
        context.DrawText(startLabel, new Point(0, size.Height - startLabel.Height));
        var endLabel = Label(string.Create(CultureInfo.CurrentCulture, $"{Math.Max(DurationSeconds, 0):N1} s"), text);
        context.DrawText(endLabel, new Point(Math.Max(0, size.Width - endLabel.Width), size.Height - endLabel.Height));
    }

    private double SecondsAt(double x) => AllocationTimelineGeometry.SecondsAt(x, Bounds.Width, Buckets, BucketSeconds, DurationSeconds);

    private double XOf(double seconds) => AllocationTimelineGeometry.XOf(seconds, Bounds.Width, Buckets, BucketSeconds, DurationSeconds);

    private static FormattedText Label(string text, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, brush);

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
}
