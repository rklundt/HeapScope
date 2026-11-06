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


using System.Xml.Linq;
using HeapScope.Contracts;

namespace HeapScope.UI.Tests;

/// <summary>
/// The Attribution Notice on the main screen (ADDITIONAL-TERMS.md 1.2 and 1.3): present, linked to the repository,
/// on an opaque theme surface, in the body font size, never hidden or faded, and at least 4.5:1 contrast in both themes.
/// Moved to the Avalonia markup in sprint 2.3.6.
/// </summary>
public class AttributionTests
{
    private static readonly XNamespace s_avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace s_x = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace s_controls = "using:HeapScope.UI.Controls";

    // Attributes that could shrink, fade, hide or clip the notice.
    private static readonly string[] s_forbidden =
        ["FontSize", "Opacity", "IsVisible", "Height", "MaxHeight", "RenderTransform", "Clip", "Classes", "Theme"];

    [Fact]
    public void Main_window_shows_the_attribution_notice_linked_to_the_repository()
    {
        var window = XDocument.Load(Path.Combine(global::HeapScope.Tests.Shared.RepoRoot.Path, "src", "HeapScope.UI", "Views", "MainWindow.axaml"));
        var border = window.Descendants(s_avalonia + "Border").Single(e => (string?)e.Attribute(s_x + "Name") == "Attribution");

        // Docked directly in the window's main panel, always shown.
        border.Parent!.Name.ShouldBe(s_avalonia + "DockPanel");
        border.Parent.Parent!.Name.ShouldBe(s_avalonia + "Window");
        ((string?)border.Attribute("Background")).ShouldBe("{DynamicResource Brush.Surface}");

        var runs = border.Descendants(s_avalonia + "Run").ToList();
        string.Concat(runs.Select(r => (string?)r.Attribute("Text"))).ShouldBe(ProductInfo.Attribution);

        var link = runs.Single(r => (string?)r.Attribute("Classes") == "link");
        ((string?)link.Attribute("Text")).ShouldBe(ProductInfo.Name);
        ((string?)link.Attribute("Foreground")).ShouldBe("{DynamicResource Brush.Text}");

        var text = border.Descendants(s_controls + "AttributionText").Single();
        ((string?)text.Attribute("LinkUri")).ShouldBe(ProductInfo.RepositoryUrl);
        ((string?)text.Attribute("Foreground")).ShouldBe("{DynamicResource Brush.Text}");
        ((string?)text.Attribute("Background")).ShouldBe("{DynamicResource Brush.Surface}");

        foreach (var element in border.DescendantsAndSelf().Where(e => e.Name != s_avalonia + "Run"))
        {
            var names = element.Attributes().Select(a => a.Name.LocalName).ToList();
            names.Intersect(s_forbidden).ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData("Light.axaml")]
    [InlineData("Dark.axaml")]
    public void Attribution_colors_are_opaque_with_at_least_AA_contrast(string theme)
    {
        var brushes = ThemeTests.Brushes(theme);

        Contrast.IsOpaque(brushes["Brush.Text"]).ShouldBeTrue();
        Contrast.IsOpaque(brushes["Brush.Surface"]).ShouldBeTrue();
        Contrast.Ratio(brushes["Brush.Text"], brushes["Brush.Surface"]).ShouldBeGreaterThanOrEqualTo(4.5);
    }
}
