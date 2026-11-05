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


using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Media;
using Avalonia.Styling;
using HeapScope.UI.Services;

namespace HeapScope.UI.Tests;

/// <summary>The light and dark themes (sprint 1.3, Avalonia since 2.3.6).</summary>
public partial class ThemeTests(UiSession ui)
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

    /// <summary>ADR-040: text stays readable on every surface it sits on, the selection included.</summary>
    [Theory]
    [InlineData("Light.axaml")]
    [InlineData("Dark.axaml")]
    public void Text_colors_meet_4_5_to_1_on_every_surface(string theme)
    {
        var brushes = Brushes(theme);
        foreach (var surface in new[] { "Window", "Surface", "Rail", "Header", "AltRow", "Selection" })
        {
            foreach (var text in new[] { "Text", "Subtle", "Accent", "Growth", "Shrink" })
            {
                Contrast.Ratio(brushes["Brush." + text], brushes["Brush." + surface]).ShouldBeGreaterThanOrEqualTo(4.5, $"{text} on {surface}");
            }
        }

        foreach (var (text, surface) in new[]
        {
            ("Brush.OnAccent", "Brush.Accent"),
            ("Brush.Window", "Brush.Growth"),
            ("Brush.Text", "Brush.Note.Background"),
            ("Brush.Text", "Brush.Info.Background"),
            ("Brush.Text", "Brush.Error.Background"),
            ("Brush.Text", "Icicle.Bar1"),
            ("Brush.Text", "Icicle.Bar2"),
            ("Icicle.PathText", "Icicle.Path"),
        })
        {
            Contrast.Ratio(brushes[text], brushes[surface]).ShouldBeGreaterThanOrEqualTo(4.5, $"{text} on {surface}");
        }
    }

    /// <summary>ADR-040: an input's edge can be seen (WCAG 1.4.11 asks 3:1 of what identifies a control).</summary>
    [Theory]
    [InlineData("Light.axaml")]
    [InlineData("Dark.axaml")]
    public void Control_edges_meet_3_to_1(string theme)
    {
        var brushes = Brushes(theme);
        Contrast.Ratio(brushes["Brush.BorderStrong"], brushes["Brush.Surface"]).ShouldBeGreaterThanOrEqualTo(3);
        Contrast.Ratio(brushes["TextControlBorderBrush"], brushes["Brush.Surface"]).ShouldBeGreaterThanOrEqualTo(3);
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

    /// <summary>ADR-040: an icon named in the markup that does not exist would simply draw nothing.</summary>
    [Fact]
    public void Every_icon_named_in_the_views_exists()
    {
        var keys = Icons();
        var views = Path.Combine(global::HeapScope.Tests.Shared.RepoRoot.Path, "src", "HeapScope.UI", "Views");
        var named = Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(file => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "(?:ButtonIcon\\.)?Icon=\"([A-Za-z]+)\""))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        named.ShouldNotBeEmpty();
        foreach (var name in named)
        {
            keys.ShouldContain($"Icon.{name}.Line");
        }
    }

    /// <summary>The sidebar draws each page's icon by its title, and the finished jobs' rows by how they ended.</summary>
    [Theory]
    [InlineData("Home")]
    [InlineData("Snapshots")]
    [InlineData("Allocations")]
    [InlineData("Processes")]
    [InlineData("Live")]
    [InlineData("Jobs")]
    [InlineData("About")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Canceled")]
    public void Every_page_and_job_state_has_its_icon(string name) =>
        Icons().ShouldContain($"Icon.{name}.Line");

    private static HashSet<string> Icons()
    {
        var path = Path.Combine(global::HeapScope.Tests.Shared.RepoRoot.Path, "src", "HeapScope.UI", "Themes", "Icons.axaml");
        return [.. XDocument.Load(path).Root!.Elements().Select(e => (string?)e.Attribute(s_x + "Key") ?? string.Empty)];
    }

    /// <summary>ADR-040: the keys Fluent's controls paint with hold the tokens' values, so 4.5:1 on a token holds on screen.</summary>
    [Theory]
    [InlineData("Light.axaml")]
    [InlineData("Dark.axaml")]
    public void Fluent_s_keys_hold_their_tokens_values(string theme)
    {
        var brushes = Brushes(theme);
        foreach (var key in brushes.Keys.Where(static k => !k.Contains('.', StringComparison.Ordinal)))
        {
            var token = key switch
            {
                _ when key.Contains("Background", StringComparison.Ordinal) && key.Contains("Selected", StringComparison.Ordinal) => "Brush.Selection",
                "DataGridColumnHeaderBackgroundBrush" => "Brush.Header",
                "TabItemHeaderSelectedPipeFill" => "Brush.Accent",
                "TextControlBorderBrush" or "ComboBoxBorderBrush" => "Brush.BorderStrong",
                _ when key.StartsWith("TabItemHeaderForegroundUnselected", StringComparison.Ordinal) => "Brush.Subtle",
                _ when key.Contains("ForegroundSelected", StringComparison.Ordinal) => "Brush.Text",
                _ => throw new ShouldAssertException($"{key} has no token here: add it to this list"),
            };
            brushes[key].ShouldBe(brushes[token], $"{key} is {token}");
        }
    }

    [Fact]
    public void A_selected_row_s_background_is_not_made_translucent()
    {
        var styles = File.ReadAllText(Path.Combine(global::HeapScope.Tests.Shared.RepoRoot.Path, "src", "HeapScope.UI", "Themes", "Styles.axaml"));
        var opacities = OpacityKey().Matches(styles);
        opacities.Count.ShouldBeGreaterThanOrEqualTo(4);
        foreach (Match opacity in opacities)
        {
            opacity.Groups["value"].Value.ShouldBe("1", opacity.Value);
        }
    }

    /// <summary>An Avalonia update can rename a key, and then the override silently does nothing.</summary>
    [Fact]
    public void The_keys_set_for_Fluent_s_controls_still_exist_in_Fluent()
    {
        var assemblies = new[] { typeof(global::Avalonia.Themes.Fluent.FluentTheme).Assembly, typeof(global::Avalonia.Controls.DataGrid).Assembly }
            .Select(static a => File.ReadAllBytes(a.Location))
            .ToArray();
        var styles = File.ReadAllText(Path.Combine(global::HeapScope.Tests.Shared.RepoRoot.Path, "src", "HeapScope.UI", "Themes", "Styles.axaml"));
        var keys = Brushes("Light.axaml").Keys.Where(static k => !k.Contains('.', StringComparison.Ordinal))
            .Concat(OpacityKey().Matches(styles).Select(static m => m.Groups["key"].Value));
        foreach (var key in keys)
        {
            var name = System.Text.Encoding.Unicode.GetBytes(key);
            assemblies.Any(bytes => bytes.AsSpan().IndexOf(name) >= 0).ShouldBeTrue($"{key} is not in Fluent or the DataGrid's theme");
        }
    }

    /// <summary>ADR-040: the theme files state the colors. A color written in a view or a control would not follow a theme.</summary>
    [Fact]
    public void Views_and_controls_state_no_color()
    {
        var ui = Path.Combine(global::HeapScope.Tests.Shared.RepoRoot.Path, "src", "HeapScope.UI");
        var files = Directory.EnumerateFiles(Path.Combine(ui, "Views"), "*.axaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(ui, "Controls"), "*.cs", SearchOption.AllDirectories));
        foreach (var file in files)
        {
            // The license header has no color; the pattern is for values, in XAML and in code.
            ColorLiteral().Match(File.ReadAllText(file)).Value.ShouldBeEmpty(Path.GetFileName(file));
        }
    }

    [Theory]
    [InlineData("Light", "Light.axaml")]
    [InlineData("Dark", "Dark.axaml")]
    public void Fluent_s_accent_is_the_theme_s_accent(string palette, string theme)
    {
        var app = XDocument.Load(Path.Combine(global::HeapScope.Tests.Shared.RepoRoot.Path, "src", "HeapScope.UI", "App.axaml"));
        var accent = app.Descendants().Single(e => e.Name.LocalName == "ColorPaletteResources" && (string?)e.Attribute(s_x + "Key") == palette);
        ((string)accent.Attribute("Accent")!).ShouldBe(Brushes(theme)["Brush.Accent"]);
    }

    [GeneratedRegex(@"#[0-9A-Fa-f]{6,8}\b|Color\.From(?:Argb|Rgb)\(|Color\.Parse\(")]
    private static partial Regex ColorLiteral();

    [GeneratedRegex(@"<x:Double x:Key=""(?<key>DataGridRowSelected\w*Opacity)"">(?<value>[^<]+)</x:Double>")]
    private static partial Regex OpacityKey();

    /// <summary>A theme file's brushes: key to color (#AARRGGBB).</summary>
    internal static Dictionary<string, string> Brushes(string file)
    {
        var path = Path.Combine(global::HeapScope.Tests.Shared.RepoRoot.Path, "src", "HeapScope.UI", "Themes", file);
        return XDocument.Load(path).Root!.Elements()
            .Where(e => e.Name.LocalName == "SolidColorBrush")
            .ToDictionary(e => (string)e.Attribute(s_x + "Key")!, e => (string)e.Attribute("Color")!, StringComparer.Ordinal);
    }
}
