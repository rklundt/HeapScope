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

        texts.Count.ShouldBe(27);
        foreach (var text in texts)
        {
            sources.ShouldContain("InfoTexts." + text.Name, customMessage: text.Name);
            ((string)text.GetRawConstantValue()!).ShouldNotBeNullOrWhiteSpace();
        }
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
