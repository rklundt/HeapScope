using System.Globalization;
using System.Windows;
using System.Windows.Media;
using HeapScope.Application.Monitoring;

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

    /// <summary>
    /// Theme brush keys of the layers, bottom to top: the heap kinds as of the last GC, then what was allocated since
    /// (gray), so the top edge is the heap now (review of 1.7).
    /// </summary>
    public static readonly string[] LayerKeys = ["HeapKind.Gen0", "HeapKind.Gen1", "HeapKind.Gen2", "HeapKind.LargeObjectHeap", "HeapKind.PinnedObjectHeap", "HeapKind.Unknown"];

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

    /// <summary>
    /// The top edge of each layer, bottom layer first: x spans the samples' time (left to right), y the stacked bytes
    /// (0 at the bottom, <paramref name="maxBytes"/> at the top).
    /// </summary>
    public static IReadOnlyList<Point[]> Layers(IReadOnlyList<HeapSample> samples, double width, double height, out long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var layers = new Point[LayerKeys.Length][];
        for (var k = 0; k < layers.Length; k++)
        {
            layers[k] = new Point[samples.Count];
        }

        maxBytes = 1;
        foreach (var s in samples)
        {
            maxBytes = Math.Max(maxBytes, Stack(s)[^1]);
        }

        if (samples.Count == 0)
        {
            return layers;
        }

        var start = samples[0].At;
        var span = Math.Max(1, (samples[^1].At - start).TotalSeconds);
        for (var i = 0; i < samples.Count; i++)
        {
            var x = samples.Count == 1 ? width : (samples[i].At - start).TotalSeconds / span * width;
            var stack = Stack(samples[i]);
            for (var k = 0; k < layers.Length; k++)
            {
                layers[k][i] = new Point(x, height - (stack[k] / (double)maxBytes * height));
            }
        }

        return layers;
    }

    /// <summary>The x of a moment in the samples' span, or null when it falls outside.</summary>
    public static double? XOf(IReadOnlyList<HeapSample> samples, DateTimeOffset at, double width)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 2 || at < samples[0].At || at > samples[^1].At)
        {
            return null;
        }

        return (at - samples[0].At).TotalSeconds / (samples[^1].At - samples[0].At).TotalSeconds * width;
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

        var layers = Layers(samples, width, height, out var maxBytes);
        for (var k = layers.Count - 1; k >= 0; k--)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(layers[k][0].X, height), isFilled: true, isClosed: true);
                context.PolyLineTo(layers[k], isStroked: false, isSmoothJoin: false);
                context.LineTo(new Point(layers[k][^1].X, height), isStroked: false, isSmoothJoin: false);
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(TryFindResource(LayerKeys[k]) as Brush ?? Brushes.Gray, null, geometry);
        }

        var text = TryFindResource("Brush.Text") as Brush ?? Brushes.Black;
        var marker = new Pen(text, 1) { DashStyle = DashStyles.Dash };
        marker.Freeze();
        foreach (var at in Markers ?? [])
        {
            if (XOf(samples, at, width) is { } x)
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

    /// <summary>Cumulative bytes per layer (gen0, +gen1, +gen2, +LOH, +POH, +allocated since the last GC).</summary>
    private static long[] Stack(HeapSample s)
    {
        var stack = new long[6];
        stack[0] = s.Gen0Bytes;
        stack[1] = stack[0] + s.Gen1Bytes;
        stack[2] = stack[1] + s.Gen2Bytes;
        stack[3] = stack[2] + s.LargeObjectHeapBytes;
        stack[4] = stack[3] + s.PinnedObjectHeapBytes;
        stack[5] = stack[4] + s.AllocatedSinceGcBytes;
        return stack;
    }
}
