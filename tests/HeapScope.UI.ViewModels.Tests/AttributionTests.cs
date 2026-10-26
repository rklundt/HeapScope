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
using System.IO;
using System.Xml.Linq;
using HeapScope.Contracts;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>
/// The Attribution Notice on the main screen (ADDITIONAL-TERMS.md 1.2 and 1.3): present, linked to the repository,
/// on an opaque theme surface, in the body font size, never hidden or faded, and at least 4.5:1 contrast in both themes.
/// </summary>
public class AttributionTests
{
    private static readonly XNamespace s_wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace s_x = "http://schemas.microsoft.com/winfx/2006/xaml";

    // Attributes that could shrink, fade, hide or clip the notice.
    private static readonly string[] s_forbidden =
        ["FontSize", "Opacity", "Visibility", "Height", "MaxHeight", "RenderTransform", "LayoutTransform", "Clip"];

    [Fact]
    public void Main_window_shows_the_attribution_notice_linked_to_the_repository()
    {
        var window = XDocument.Load(Path.Combine(global::HeapScope.Tests.Shared.RepoRoot.Path, "src", "HeapScope.UI.Wpf", "Views", "MainWindow.xaml"));
        var border = window.Descendants(s_wpf + "Border").Single(e => (string?)e.Attribute(s_x + "Name") == "Attribution");

        // Docked directly in the window's main panel, always shown.
        border.Parent!.Name.ShouldBe(s_wpf + "DockPanel");
        border.Parent.Parent!.Name.ShouldBe(s_wpf + "Window");
        ((string?)border.Attribute("Background")).ShouldBe("{DynamicResource Brush.Surface}");

        var text = border.Descendants().Where(e => e.Name == s_wpf + "Run" || e.Name == s_wpf + "Hyperlink")
            .Select(e => (string?)e.Attribute("Text") ?? e.Value);
        string.Concat(text).ShouldBe(ProductInfo.Attribution);

        var link = border.Descendants(s_wpf + "Hyperlink").Single();
        ((string?)link.Attribute("NavigateUri")).ShouldBe(ProductInfo.RepositoryUrl);
        link.Value.ShouldBe(ProductInfo.Name);

        var box = border.Descendants(s_wpf + "RichTextBox").Single();
        ((string?)box.Attribute("Foreground")).ShouldBe("{DynamicResource Brush.Text}");
        ((string?)box.Attribute("Background")).ShouldBe("{DynamicResource Brush.Surface}");
        ((string?)link.Attribute("Foreground")).ShouldBe("{DynamicResource Brush.Text}");

        foreach (var element in border.DescendantsAndSelf())
        {
            var names = element.Attributes().Select(a => a.Name.LocalName).ToList();
            names.Intersect(s_forbidden).ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData("Light.xaml")]
    [InlineData("Dark.xaml")]
    public void Attribution_colors_are_opaque_with_at_least_AA_contrast(string theme)
    {
        var brushes = XDocument.Load(Path.Combine(global::HeapScope.Tests.Shared.RepoRoot.Path, "src", "HeapScope.UI.Wpf", "Themes", theme)).Root!
            .Elements(s_wpf + "SolidColorBrush")
            .ToDictionary(e => (string)e.Attribute(s_x + "Key")!, e => (string)e.Attribute("Color")!);

        var text = Parse(brushes["Brush.Text"]);
        var surface = Parse(brushes["Brush.Surface"]);

        text.A.ShouldBe((byte)255);
        surface.A.ShouldBe((byte)255);
        Contrast(text, surface).ShouldBeGreaterThanOrEqualTo(4.5);
    }

    private static (byte A, byte R, byte G, byte B) Parse(string color)
    {
        color.Length.ShouldBe(9);
        var value = uint.Parse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    // WCAG 2.x contrast ratio.
    private static double Contrast((byte A, byte R, byte G, byte B) a, (byte A, byte R, byte G, byte B) b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance((byte A, byte R, byte G, byte B) c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Channel(byte value)
    {
        var s = value / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
