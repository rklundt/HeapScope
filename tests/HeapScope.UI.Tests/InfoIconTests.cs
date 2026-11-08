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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeapScope.Application.Monitoring;
using HeapScope.Application.Jobs;
using HeapScope.UI.Controls;
using HeapScope.UI.Services;
using HeapScope.UI.ViewModels;
using HeapScope.UI.ViewModels.Tests;
using HeapScope.UI.Views.Pages;

namespace HeapScope.UI.Tests;

/// <summary>ADR-040, sprint 3.11: a screen says what it is for, by hover, click or keyboard.</summary>
public sealed class InfoIconTests(UiSession ui)
{
    private static (InfoIcon Icon, Window Window) Shown()
    {
        var icon = new InfoIcon { Text = "What this is for." };
        var window = new Window { Width = 400, Height = 200, Content = new StackPanel { Children = { icon } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        return (icon, window);
    }

    [Fact]
    public async Task Hovering_has_the_text_and_a_screen_reader_can_reach_it()
    {
        await ui.RunAsync(() =>
        {
            var (icon, window) = Shown();
            ToolTip.GetTip(icon).ShouldBeOfType<TextBlock>().Text.ShouldBe("What this is for.");
            Avalonia.Automation.AutomationProperties.GetName(icon).ShouldNotBeNullOrEmpty();
            Avalonia.Automation.AutomationProperties.GetHelpText(icon).ShouldBe("What this is for.");
            icon.IsEffectivelyEnabled.ShouldBeTrue();
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_click_opens_it_and_Escape_closes_it()
    {
        await ui.RunAsync(() =>
        {
            var (icon, window) = Shown();
            var flyout = icon.Flyout.ShouldBeOfType<Flyout>();
            var at = icon.TranslatePoint(new Point(10, 10), window)!.Value;

            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            flyout.IsOpen.ShouldBeTrue();
            flyout.Content.ShouldBeOfType<SelectableTextBlock>().Text.ShouldBe("What this is for.");

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            flyout.IsOpen.ShouldBeFalse();
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Enter_opens_it_from_the_keyboard()
    {
        await ui.RunAsync(() =>
        {
            var (icon, window) = Shown();
            icon.Focus(NavigationMethod.Tab).ShouldBeTrue();

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            icon.Flyout.ShouldBeOfType<Flyout>().IsOpen.ShouldBeTrue();
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Its_icon_is_in_the_theme()
    {
        var icons = File.ReadAllText(Path.Combine(global::HeapScope.Tests.Shared.RepoRoot.Path, "src", "HeapScope.UI", "Themes", "Icons.axaml"));
        icons.ShouldContain("x:Key=\"Icon.Info.Line\"");
    }

    /// <summary>An approved text no screen shows would be a promise nobody keeps: each is used, in the views or by a view model that chooses.</summary>
    [Fact]
    public void Every_approved_text_is_on_a_screen()
    {
        var src = Path.Combine(global::HeapScope.Tests.Shared.RepoRoot.Path, "src");
        var sources = string.Concat(
            Directory.EnumerateFiles(Path.Combine(src, "HeapScope.UI", "Views"), "*.axaml", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(Path.Combine(src, "HeapScope.UI.ViewModels", "ViewModels"), "*.cs").Where(static f => !f.EndsWith("InfoTexts.cs", StringComparison.Ordinal)))
                .Select(File.ReadAllText));
        var texts = typeof(InfoTexts).GetFields().Where(static f => f.IsLiteral).ToList();

        texts.Count.ShouldBe(48);
        foreach (var text in texts)
        {
            sources.ShouldContain("InfoTexts." + text.Name, customMessage: text.Name);
            ((string)text.GetRawConstantValue()!).ShouldNotBeNullOrWhiteSpace();
        }
    }

    /// <summary>
    /// The owner approved the wording table (`docs/design/info-text.md`), not the code: each group icon says its row word for word, a
    /// line per control, and each single control's tooltip is on a screen word for word.
    /// </summary>
    [Fact]
    public void The_screens_say_what_the_owner_approved()
    {
        var repo = global::HeapScope.Tests.Shared.RepoRoot.Path;
        var table = File.ReadAllText(Path.Combine(repo, "docs", "design", "info-text.md")).ReplaceLineEndings("\n");
        var groups = Rows(Between(table, "## Table 2", "## Table 3"));
        var singles = Rows(Between(table, "## Table 3", "## Evidence"));
        var texts = typeof(InfoTexts).GetFields().Where(static f => f.IsLiteral).Select(static f => (string)f.GetRawConstantValue()!).ToHashSet();
        var src = Path.Combine(repo, "src", "HeapScope.UI");
        var screens = string.Concat(
            Directory.EnumerateFiles(Path.Combine(src, "Views"), "*.axaml", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(Path.Combine(src, "Controls"), "*.cs"))
                .Select(File.ReadAllText))
            .Replace("&quot;", "\"", StringComparison.Ordinal).Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal);

        groups.Count.ShouldBe(20);
        foreach (var row in groups)
        {
            texts.ShouldContain(row[2].Replace("<br>", "\n", StringComparison.Ordinal), customMessage: row[0] + ", " + row[1]);
        }

        singles.Count.ShouldBe(43);
        foreach (var row in singles)
        {
            // A whole quoted string: a view's ToolTip.Tip, or a constant a control sets in code (the export menu's).
            screens.ShouldContain("\"" + row[2] + "\"", customMessage: row[0] + ", " + row[1]);
        }

        static string Between(string text, string from, string to)
        {
            var start = text.IndexOf(from, StringComparison.Ordinal);
            return text[start..text.IndexOf(to, start, StringComparison.Ordinal)];
        }

        static List<string[]> Rows(string section) =>
            section.Split('\n').Where(static l => l.StartsWith("| ", StringComparison.Ordinal) && !l.StartsWith("| Page", StringComparison.Ordinal))
                .Select(static l => l.Trim('|').Split(" | ").Select(static c => c.Trim()).ToArray()).ToList();
    }

    /// <summary>An icon in a header acts on its own: a click opens its text and leaves the expander or tab as it was.</summary>
    [Fact]
    public async Task An_icon_in_an_expander_header_opens_without_expanding_it()
    {
        await ui.RunAsync(() =>
        {
            var icon = new InfoIcon { Text = "What this is for." };
            var expander = new Expander { Header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { new TextBlock { Text = "Rules" }, icon } }, Content = new TextBlock { Text = "Inside" } };
            var window = Show(expander);

            Click(window, icon);

            icon.Flyout.ShouldBeOfType<Flyout>().IsOpen.ShouldBeTrue();
            expander.IsExpanded.ShouldBeFalse();
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task An_icon_in_a_tab_header_opens_without_switching_tabs()
    {
        await ui.RunAsync(() =>
        {
            var icon = new InfoIcon { Text = "What this is for." };
            var tabs = new TabControl
            {
                Items =
                {
                    new TabItem { Header = "First", Content = new TextBlock { Text = "One" } },
                    new TabItem { Header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { new TextBlock { Text = "Second" }, icon } }, Content = new TextBlock { Text = "Two" } },
                },
            };
            var window = Show(tabs);
            tabs.SelectedIndex.ShouldBe(0);

            Click(window, icon);

            icon.Flyout.ShouldBeOfType<Flyout>().IsOpen.ShouldBeTrue();
            tabs.SelectedIndex.ShouldBe(0);
            window.Close();
            return Task.CompletedTask;
        });
    }

    private static Window Show(Control content)
    {
        var window = new Window { Width = 500, Height = 300, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Click(Window window, Control control)
    {
        var at = control.TranslatePoint(new Point(8, 8), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public async Task A_page_shows_its_icon_with_its_text()
    {
        var folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await ui.RunAsync(() =>
        {
            var capture = new FakeCaptureService(folder, new JobTracker(TimeProvider.System));
            var live = new LiveViewModel(new MonitorService(capture, new SyntheticCounterSource(0, perSecond: 1)), capture, ImmediateUiDispatcher.Instance);
            ui.ClearBindingErrors();
            var window = new Window { Content = new LivePage { DataContext = live }, Width = 1000, Height = 700 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.GetVisualDescendants().OfType<InfoIcon>().First().Text.ShouldBe(InfoTexts.Live);
            ui.BindingErrors.ShouldBeEmpty();
            window.Close();
            return Task.CompletedTask;
        });
        Directory.Delete(folder, recursive: true);
    }
}
