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
using Avalonia.Data.Converters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.Controls;

/// <summary>
/// Key retention paths merged into a graph (sprint 3.2): roots at the top, the paths' types below, the objects they explain
/// at the bottom; an edge is as thick as its share of the busiest edge and says how many objects take it. The layout is
/// <see cref="KeyPathDiagram"/>'s; this control only draws it. Placed in a scroll viewer.
/// </summary>
public sealed class KeyPathDiagramView : Control
{
    public static readonly StyledProperty<KeyPathDiagram?> DiagramProperty =
        AvaloniaProperty.Register<KeyPathDiagramView, KeyPathDiagram?>(nameof(Diagram));

    /// <summary>Scales the diagram down to the width it is given (the scroll viewer around it then has no horizontal bar).</summary>
    public static readonly StyledProperty<bool> FitWidthProperty =
        AvaloniaProperty.Register<KeyPathDiagramView, bool>(nameof(FitWidth));

    /// <summary>A scroll viewer's horizontal bar for a fit-to-width diagram: off, or it would offer infinite width.</summary>
    public static FuncValueConverter<bool, ScrollBarVisibility> ScrollBarForFit { get; } =
        new(static fit => fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);

    private const double Inset = 12;

    private double _scale = 1;

    static KeyPathDiagramView()
    {
        AffectsRender<KeyPathDiagramView>(DiagramProperty, FitWidthProperty);
        AffectsMeasure<KeyPathDiagramView>(DiagramProperty, FitWidthProperty);
    }

    public KeyPathDiagramView()
    {
        // The colors are the theme's, read when drawing.
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public KeyPathDiagram? Diagram
    {
        get => GetValue(DiagramProperty);
        set => SetValue(DiagramProperty, value);
    }

    public bool FitWidth
    {
        get => GetValue(FitWidthProperty);
        set => SetValue(FitWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Diagram is not { Nodes.Count: > 0 } diagram)
        {
            return default;
        }

        _scale = FitWidth ? diagram.FitScale(availableSize.Width - (2 * Inset)) : 1;
        return new Size((diagram.Width * _scale) + (2 * Inset), (diagram.Height * _scale) + (2 * Inset));
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Diagram is not { Nodes.Count: > 0 } diagram)
        {
            return;
        }

        var text = Brush("Brush.Text") ?? Brushes.Black;
        var subtle = Brush("Brush.Subtle") ?? Brushes.Gray;
        var line = Brush("Brush.BorderStrong") ?? Brushes.Gray;
        var nodeEdge = Brush("Brush.Border") ?? Brushes.Gray;
        var accent = Brush("Brush.Accent") ?? Brushes.Blue;
        var targetText = Brush("Icicle.PathText") ?? Brushes.Black;
        var surface = Brush("Brush.Surface") ?? Brushes.White;
        var root = Brush("Icicle.Bar1") ?? Brushes.LightBlue;
        var target = Brush("Icicle.Path") ?? Brushes.Orange;
        var nodes = diagram.Nodes;
        using var scaled = context.PushTransform(Matrix.CreateTranslation(-Inset, -Inset) * Matrix.CreateScale(_scale, _scale) * Matrix.CreateTranslation(Inset, Inset));
        const double w = KeyPathDiagram.NodeWidth, h = KeyPathDiagram.NodeHeight;
        foreach (var edge in diagram.Edges)
        {
            var from = nodes[edge.From];
            var to = nodes[edge.To];
            var thickness = KeyPathDiagram.EdgeThickness(edge.Objects, diagram.MaxEdgeObjects);
            var pen = new Pen(line, thickness) { DashStyle = edge.Folded > 0 ? DashStyle.Dash : null };
            Point start, end;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                if (edge.IsBack)
                {
                    // Back up to a type met earlier: around the right side.
                    start = new Point(Inset + from.X + w, Inset + from.Y + (h / 2));
                    end = new Point(Inset + to.X + w, Inset + to.Y + (h / 2));
                    var bend = Math.Max(start.X, end.X) + (KeyPathDiagram.ColumnGap * 0.8);
                    g.BeginFigure(start, false);
                    g.CubicBezierTo(new Point(bend, start.Y), new Point(bend, end.Y), end);
                }
                else
                {
                    start = new Point(Inset + from.X + (w / 2), Inset + from.Y + h);
                    end = new Point(Inset + to.X + (w / 2), Inset + to.Y);
                    var middle = (start.Y + end.Y) / 2;
                    g.BeginFigure(start, false);
                    g.CubicBezierTo(new Point(start.X, middle), new Point(end.X, middle), end);
                }

                g.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
            var label = Label(
                string.Create(CultureInfo.CurrentCulture, $"{edge.Objects:N0}") + (edge.Folded > 0 ? string.Create(CultureInfo.CurrentCulture, $" (+{edge.Folded} framework)") : string.Empty),
                11,
                subtle,
                KeyPathDiagram.NodeWidth);
            // On the surface's color, so the edges passing behind it do not cross the digits.
            var at = new Point(((start.X + end.X) / 2) - (label.Width / 2), ((start.Y + end.Y) / 2) - (label.Height / 2));
            context.FillRectangle(surface, new Rect(at.X - 3, at.Y, label.Width + 6, label.Height));
            context.DrawText(label, at);
        }

        foreach (var node in nodes)
        {
            var rect = new Rect(Inset + node.X, Inset + node.Y, w, h);
            var fill = node.Kind switch
            {
                DiagramNodeKind.Root => root,
                DiagramNodeKind.Target => target,
                _ => surface,
            };
            context.DrawRectangle(fill, new Pen(nodeEdge, 1), rect, 4, 4);
            if (node.Kind == DiagramNodeKind.Root)
            {
                context.FillRectangle(accent, new Rect(rect.X, rect.Y, 3, rect.Height));
            }

            var nodeText = node.Kind == DiagramNodeKind.Target ? targetText : text;
            var name = Label(node.Label, 12, nodeText, w - 14);
            context.DrawText(name, new Point(rect.X + 8, rect.Y + 5));
            var count = Label(string.Create(CultureInfo.CurrentCulture, $"{node.Objects:N0} objects"), 11, node.Kind == DiagramNodeKind.Type ? subtle : nodeText, w - 14);
            context.DrawText(count, new Point(rect.X + 8, rect.Bottom - count.Height - 5));
        }
    }

    private static FormattedText Label(string value, double size, IBrush brush, double maxWidth) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
}
