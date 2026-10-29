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
using Avalonia.Media;
using Avalonia.Styling;
using HeapScope.UI.Services;

namespace HeapScope.UI.Tests;

/// <summary>The light and dark themes (sprint 1.3, Avalonia since 2.3.6).</summary>
public class ThemeTests(UiSession ui)
{
    private static readonly XNamespace s_x = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Both_themes_define_the_same_brushes()
    {
        Brushes("Light.axaml").Keys.Order(StringComparer.Ordinal).ShouldBe(Brushes("Dark.axaml").Keys.Order(StringComparer.Ordinal));
        Brushes("Light.axaml").Count.ShouldBeGreaterThan(20);
        foreach (var kind in Enum.GetValues<HeapScope.Contracts.HeapKindDto>())
        {
            Brushes("Light.axaml").ShouldContainKey("HeapKind." + kind, "every heap kind has its fixed color");
        }
    }

    [Theory]
    [InlineData(null, "Default")]
    [InlineData("", "Default")]
    [InlineData("dark", "Dark")]
    [InlineData("Light", "Light")]
    [InlineData("blue", "Default")]
    public void The_configured_theme_is_used_when_valid_otherwise_the_systems(string? configured, string expected) =>
        ThemeChooser.Choose(configured).Key.ToString().ShouldBe(expected);

    [Fact]
    public async Task The_app_resolves_each_theme_s_own_colors()
    {
        var (light, dark) = await ui.RunAsync(() =>
        {
            var app = Avalonia.Application.Current!;
            app.TryGetResource("Brush.Text", ThemeVariant.Light, out var lightText).ShouldBeTrue();
            app.TryGetResource("Brush.Text", ThemeVariant.Dark, out var darkText).ShouldBeTrue();
            return Task.FromResult((((ISolidColorBrush)lightText!).Color, ((ISolidColorBrush)darkText!).Color));
        });

        light.ToString().ShouldBe(Brushes("Light.axaml")["Brush.Text"], StringCompareShould.IgnoreCase);
        dark.ToString().ShouldBe(Brushes("Dark.axaml")["Brush.Text"], StringCompareShould.IgnoreCase);
    }

    /// <summary>A theme file's brushes: key to color (#AARRGGBB).</summary>
    internal static Dictionary<string, string> Brushes(string file)
    {
        var path = Path.Combine(global::HeapScope.Tests.Shared.RepoRoot.Path, "src", "HeapScope.UI", "Themes", file);
        return XDocument.Load(path).Root!.Elements()
            .Where(e => e.Name.LocalName == "SolidColorBrush")
            .ToDictionary(e => (string)e.Attribute(s_x + "Key")!, e => (string)e.Attribute("Color")!, StringComparer.Ordinal);
    }
}
