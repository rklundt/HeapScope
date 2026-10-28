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
using HeapScope.Application.Jobs;
using HeapScope.Application.Monitoring;
using HeapScope.Application.Snapshots;
using HeapScope.Application;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using HeapScope.UI.ViewModels;
using HeapScope.UI.ViewModels.Tests;
using HeapScope.UI.Views;
using HeapScope.UI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace HeapScope.UI.Tests;

/// <summary>
/// Every view of the app, built and laid out headless over the view models and the fake worker (sprint 2.3.6): each page
/// of the shell, and the Snapshot tab's subjects from the types grid down to one object's retention, the Compare and
/// Inspections tabs; none may log a binding failure.
/// </summary>
public sealed class ViewTests(UiSession ui) : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("heapscope-views-").FullName;

    [Fact]
    public async Task Every_page_of_the_shell_builds_and_binds_cleanly()
    {
        await ui.RunAsync(async () =>
        {
            var (shell, _) = Shell();
            ui.ClearBindingErrors();
            var window = new MainWindow(shell);
            window.Show();
            foreach (var item in shell.NavigationItems)
            {
                shell.SelectedItem = item;
                Dispatcher.UIThread.RunJobs();
                window.GetVisualDescendants().OfType<UserControl>().ShouldContain(c => c.DataContext == item.Page, $"the {item.Title} page is shown");
            }

            await shell.Home.RefreshSessionsCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            ui.BindingErrors.ShouldBeEmpty();
            window.Close();
        });
    }

    [Fact]
    public async Task The_snapshot_tab_shows_every_subject_and_the_other_tabs_bind_cleanly()
    {
        await ui.RunAsync(async () =>
        {
            var (shell, worker) = Shell();
            worker.Service.Rows.Add(["App.Order", "2", "80"]);
            worker.Service.InstanceRows.Add(["0x1000", "App.Order", "40", "Gen2", ""]);
            worker.Service.ReferenceRows.Add(["Customer", "0x3000", "App.Customer", "60"]);
            var snapshots = shell.Snapshots;
            ui.ClearBindingErrors();
            var window = new MainWindow(shell);
            window.Show();
            shell.SelectedItem = shell.NavigationItems.Single(i => i.Page == snapshots);

            var dump = Path.Combine(_folder, "app.dmp");
            await File.WriteAllBytesAsync(dump, [1]);
            await snapshots.OpenPathAsync(dump);
            await snapshots.Types.QueryTask;
            await Settle(snapshots.Types.Rows);
            Grid(window, "TypesGrid").IsEffectivelyVisible.ShouldBeTrue();

            var navigator = snapshots.Navigator;
            snapshots.Types.OpenRowCommand!.Execute(snapshots.Types.Rows.GetRow(0));
            var instances = navigator.Current.ShouldBeOfType<InstancesViewModel>();
            await instances.QueryTask;
            await Settle(instances.Rows);
            Grid(window, "InstancesGrid").IsEffectivelyVisible.ShouldBeTrue();

            instances.OpenRowCommand!.Execute(instances.Rows.GetRow(0));
            var obj = navigator.Current.ShouldBeOfType<ObjectViewModel>();
            await obj.LoadTask;
            await Settle(obj.ReferenceRows);
            foreach (var tab in new[] { 1, 2, 0 })
            {
                obj.ReferencesTab = tab;
                Dispatcher.UIThread.RunJobs();
            }

            Grid(window, "ReferencesGrid").IsEffectivelyVisible.ShouldBeTrue();
            navigator.BackCommand.Execute(null);
            instances.ReferencedByCommand!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            navigator.Current.ShouldBeOfType<TypeReferrersViewModel>();
            Grid(window, "TypeReferrersGrid").IsEffectivelyVisible.ShouldBeTrue();
            navigator.BackCommand.Execute(null);
            instances.WhyAliveCommand!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Grid(window, "TypePathsGrid").IsEffectivelyVisible.ShouldBeTrue();

            foreach (var tab in new[] { 1, 2, 3, 0 })
            {
                snapshots.SelectedTab = tab;
                Dispatcher.UIThread.RunJobs();
            }

            ui.BindingErrors.ShouldBeEmpty();
            window.Close();
        });
    }

    [Fact]
    public async Task The_retainers_tab_expands_with_the_keys_and_a_type_rows_menu_opens_its_set()
    {
        await ui.RunAsync(async () =>
        {
            var (shell, worker) = Shell();
            worker.Service.Rows.Add(["App.Order", "2", "80"]);
            worker.Service.TreeNodes.AddRange(
            [
                new FakeTreeNode("o:100", string.Empty, "App.Cache", 0x100, 800),
                new FakeTreeNode("o:200", "o:100", "App.Entry", 0x200, 400),
                new FakeTreeNode("o:400", string.Empty, "App.Other", 0x400, 50),
            ]);
            worker.Service.SetRows.AddRange([("App.Order", 2, 80), ("System.Byte[]", 2, 2000)]);
            var snapshots = shell.Snapshots;
            ui.ClearBindingErrors();
            var window = new MainWindow(shell) { Width = 1400, Height = 900 };
            window.Show();
            shell.SelectedItem = shell.NavigationItems.Single(i => i.Page == snapshots);
            var dump = Path.Combine(_folder, "app.dmp");
            await File.WriteAllBytesAsync(dump, [1]);
            await snapshots.OpenPathAsync(dump);
            await snapshots.Types.QueryTask;
            await Settle(snapshots.Types.Rows);

            // A type row's menu: right-click selects the row, then "Objects retained by this set".
            var types = Grid(window, "TypesGrid");
            var typeRow = types.GetVisualDescendants().OfType<DataGridRow>().First();
            var at = typeRow.TranslatePoint(new Avalonia.Point(20, typeRow.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, Avalonia.Input.MouseButton.Right);
            window.MouseUp(at, Avalonia.Input.MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            types.SelectedItem.ShouldBeSameAs(snapshots.Types.Rows.GetRow(0), "a right-click selects the row its menu acts on");
            var item = types.ContextMenu.ShouldNotBeNull().Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Objects retained by this set");
            item.Command.ShouldNotBeNull().Execute(item.CommandParameter);
            types.ContextMenu.Close();

            var retainers = snapshots.Retainers;
            await retainers.QueryTask;
            await retainers.IcicleTask;
            await retainers.Set.QueryTask;
            await Settle(retainers.Rows);
            await Settle(retainers.Set.Rows);
            snapshots.Tab.ShouldBe(SnapshotTabs.Retainers);
            worker.Service.SetQueries.ShouldHaveSingleItem().TypeName.ShouldBe("App.Order");
            Grid(window, "SetGrid").IsEffectivelyVisible.ShouldBeTrue();
            window.GetVisualDescendants().OfType<Controls.IcicleChart>().ShouldHaveSingleItem().Bounds.Width.ShouldBeGreaterThan(0);

            // Right expands the selected row in place, Left collapses it.
            var tree = Grid(window, "TreeGrid");
            tree.IsEffectivelyVisible.ShouldBeTrue();
            tree.SelectedIndex = 0;
            tree.Focus();
            window.KeyPressQwerty(Avalonia.Input.PhysicalKey.ArrowRight, Avalonia.Input.RawInputModifiers.None);
            window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.ArrowRight, Avalonia.Input.RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await retainers.QueryTask;
            await Settle(retainers.Rows);
            retainers.Rows.Count.ShouldBe(3);

            // The expanded row stays selected in the new rows, so Left works without selecting it again (the 2.4 review).
            await retainers.SelectTask;
            Dispatcher.UIThread.RunJobs();
            tree.SelectedItem.ShouldBeSameAs(retainers.Rows.GetRow(0));
            window.KeyPressQwerty(Avalonia.Input.PhysicalKey.ArrowLeft, Avalonia.Input.RawInputModifiers.None);
            window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.ArrowLeft, Avalonia.Input.RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await retainers.QueryTask;
            await Settle(retainers.Rows);
            retainers.Rows.Count.ShouldBe(2);

            ui.BindingErrors.ShouldBeEmpty();
            window.Close();
        });
    }

    [Fact]
    public async Task The_inspections_tab_groups_the_leak_detectors_and_offers_the_earlier_snapshot_as_baseline()
    {
        await ui.RunAsync(async () =>
        {
            var (shell, worker) = Shell();
            worker.Service.Inspections.Insert(0, new InspectionInfo("growing-static-collections", "Growing static collections", "Grows.", "Bound it.", null, InspectionKinds.Leak, UsesBaseline: true));
            var earlier = DateTimeOffset.UtcNow.AddMinutes(-5);
            worker.Service.CapturedAt = path => path.EndsWith("a.dmp", StringComparison.Ordinal) ? earlier : earlier.AddMinutes(2);
            var snapshots = shell.Snapshots;
            ui.ClearBindingErrors();
            var window = new MainWindow(shell) { Width = 1400, Height = 900 };
            window.Show();
            shell.SelectedItem = shell.NavigationItems.Single(i => i.Page == snapshots);
            foreach (var name in new[] { "a.dmp", "b.dmp" })
            {
                var dump = Path.Combine(_folder, name);
                await File.WriteAllBytesAsync(dump, [1]);
                await snapshots.OpenPathAsync(dump);
            }

            snapshots.Tab = SnapshotTabs.Inspections;
            await snapshots.Inspections.RunTask;
            Dispatcher.UIThread.RunJobs();

            var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "BaselinePicker");
            picker.IsEffectivelyVisible.ShouldBeTrue();
            picker.SelectedItem.ShouldBeOfType<BaselineChoice>().Text.ShouldStartWith("a.dmp");
            window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("Leak detectors");
            window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("Memory waste");

            // A click on a group heading selects nothing (2.6 review L13); a click on the tile below it does.
            var selected = snapshots.Inspections.Selected;
            var heading = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Memory waste" && t.Classes.Contains("groupHeading"));
            Click(window, heading);
            snapshots.Inspections.Selected.ShouldBe(selected);
            var tile = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Duplicate strings");
            Click(window, tile);
            snapshots.Inspections.Selected!.Title.ShouldBe("Duplicate strings");

            ui.BindingErrors.ShouldBeEmpty();
            window.Close();
        });
    }

    [Fact]
    public async Task The_allocations_page_shows_a_trace_and_its_tree_expands_with_the_keys()
    {
        await ui.RunAsync(async () =>
        {
            var (shell, worker) = Shell();
            var allocations = shell.Allocations.ShouldNotBeNull();
            ui.ClearBindingErrors();
            var window = new MainWindow(shell) { Width = 1400, Height = 900 };
            window.Show();
            var path = Path.Combine(_folder, "app.nettrace");
            await File.WriteAllBytesAsync(path, [1]);

            await allocations.OpenPathAsync(path);
            var trace = allocations.Selected.ShouldNotBeNull();
            await trace.LoadTask;
            await trace.Types.QueryTask;
            await trace.Tree.QueryTask;
            await Settle(trace.Types.Rows);
            await Settle(trace.Tree.Rows);

            shell.SelectedItem.Page.ShouldBe(allocations, "opening a trace shows the Allocations page");
            var timeline = window.GetVisualDescendants().OfType<Controls.AllocationTimeline>().ShouldHaveSingleItem();
            timeline.Buckets!.Count.ShouldBe(2);
            timeline.Bounds.Width.ShouldBeGreaterThan(0);
            Grid(window, "TypesGrid").GetVisualDescendants().OfType<DataGridRow>().Count().ShouldBe(2);
            Grid(window, "StacksGrid").GetVisualDescendants().OfType<DataGridRow>().Count().ShouldBe(2);

            // A drag across the timeline selects an interval for every view.
            var left = timeline.TranslatePoint(new Point(timeline.Bounds.Width * 0.1, 20), window)!.Value;
            var right = timeline.TranslatePoint(new Point(timeline.Bounds.Width * 0.6, 20), window)!.Value;
            window.MouseDown(left, Avalonia.Input.MouseButton.Left);
            window.MouseMove(right);
            window.MouseUp(right, Avalonia.Input.MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            trace.Selection.ShouldNotBeNull();
            await trace.LoadTask;
            await trace.Tree.QueryTask;
            await Settle(trace.Tree.Rows);
            worker.Service.StackQueries[^1].ToSeconds.ShouldNotBeNull();

            // Right expands the selected back trace in place, Left collapses it.
            var tree = Grid(window, "TreeGrid");
            tree.SelectedIndex = 0;
            tree.Focus();
            window.KeyPressQwerty(Avalonia.Input.PhysicalKey.ArrowRight, Avalonia.Input.RawInputModifiers.None);
            window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.ArrowRight, Avalonia.Input.RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await trace.Tree.QueryTask;
            await Settle(trace.Tree.Rows);
            trace.Tree.Rows.Count.ShouldBe(3);
            await trace.Tree.SelectTask;
            Dispatcher.UIThread.RunJobs();
            window.KeyPressQwerty(Avalonia.Input.PhysicalKey.ArrowLeft, Avalonia.Input.RawInputModifiers.None);
            window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.ArrowLeft, Avalonia.Input.RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await trace.Tree.QueryTask;
            await Settle(trace.Tree.Rows);
            trace.Tree.Rows.Count.ShouldBe(2);

            // The call tree.
            trace.ShowCallTree = true;
            await trace.Tree.QueryTask;
            await Settle(trace.Tree.Rows);
            trace.Tree.Rows.GetRow(0).Name.ShouldBe("App.Main");

            ui.BindingErrors.ShouldBeEmpty();
            window.Close();
        });
    }

    [Fact]
    public async Task The_live_page_charts_samples_and_binds_cleanly()
    {
        await ui.RunAsync(() =>
        {
            var capture = new FakeCaptureService(_folder, new JobTracker(TimeProvider.System));
            var live = new LiveViewModel(new MonitorService(capture, new SyntheticCounterSource(0, perSecond: 1)), capture, ImmediateUiDispatcher.Instance);
            ui.ClearBindingErrors();
            var window = new Window { Content = new LivePage { DataContext = live }, Width = 1000, Height = 700 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.GetVisualDescendants().OfType<Controls.TimelineChart>().ShouldHaveSingleItem().Bounds.Width.ShouldBeGreaterThan(0);
            ui.BindingErrors.ShouldBeEmpty();
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Where_capture_is_not_available_the_processes_page_says_why()
    {
        await ui.RunAsync(async () =>
        {
            var (shell, _) = Shell(unavailable: "Capturing from HeapScope runs on Windows for now.");
            var window = new MainWindow(shell);
            window.Show();
            var processes = shell.NavigationItems.Single(i => i.Page is ProcessesViewModel);
            shell.SelectedItem = processes;
            await ((ProcessesViewModel)processes.Page).RefreshCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            window.GetVisualDescendants().OfType<SelectableTextBlock>()
                .ShouldContain(t => t.Text == "Capturing from HeapScope runs on Windows for now." && t.IsEffectivelyVisible);
            window.Close();
        });
    }

    [Fact]
    public async Task Every_page_tab_and_subject_is_reached_with_the_keyboard_only()
    {
        // Sprint 2.7 acceptance: every view reachable by keyboard; the path survives view switches.
        await ui.RunAsync(async () =>
        {
            var interaction = new FakeUserInteraction { SaveFile = Path.Combine(_folder, "types.csv") };
            var (shell, worker) = Shell(interaction: interaction);
            worker.Service.Rows.Add(["App.Order", "2", "80"]);
            worker.Service.InstanceRows.Add(["0x1000", "App.Order", "40", "Gen2", ""]);
            worker.Service.ReferenceRows.Add(["Customer", "0x3000", "App.Customer", "60"]);
            var snapshots = shell.Snapshots;
            ui.ClearBindingErrors();
            var window = new MainWindow(shell);
            window.Show();
            var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            PhysicalKey[] digits = [PhysicalKey.Digit1, PhysicalKey.Digit2, PhysicalKey.Digit3, PhysicalKey.Digit4, PhysicalKey.Digit5, PhysicalKey.Digit6, PhysicalKey.Digit7, PhysicalKey.Digit8, PhysicalKey.Digit9];

            // Pages by number, in the sidebar's order.
            for (var i = shell.NavigationItems.Count - 1; i >= 0; i--)
            {
                Press(window, digits[i], command);
                shell.SelectedItem.ShouldBe(shell.NavigationItems[i]);
            }

            var dump = Path.Combine(_folder, "app.dmp");
            await File.WriteAllBytesAsync(dump, [1]);
            await snapshots.OpenPathAsync(dump);
            await snapshots.Types.QueryTask;
            await Settle(snapshots.Types.Rows);

            // The Snapshots tabs, from any page.
            shell.ShowHelp();
            foreach (var tab in Enum.GetValues<SnapshotTabs>().Reverse())
            {
                Press(window, digits[(int)tab], RawInputModifiers.Alt);
                (shell.SelectedItem.Page, snapshots.Tab).ShouldBe((snapshots, tab));
            }

            // The filter, then the types grid: Down selects a row, Enter opens its objects, Enter on one opens it.
            Press(window, PhysicalKey.F, command);
            var filter = window.FocusManager!.GetFocusedElement().ShouldBeOfType<TextBox>();
            filter.Name.ShouldBe("FilterBox");
            var types = Grid(window, "TypesGrid");
            types.Focus();
            Press(window, PhysicalKey.ArrowDown);
            Press(window, PhysicalKey.Enter);
            var instances = snapshots.Navigator.Current.ShouldBeOfType<InstancesViewModel>();
            await instances.QueryTask;
            await Settle(instances.Rows);
            var instancesGrid = Grid(window, "InstancesGrid");
            instancesGrid.Focus();
            Press(window, PhysicalKey.ArrowDown);

            // Bookmark the selected row, open it, and come back along the path.
            Press(window, PhysicalKey.D, command);
            await WaitFor(() => snapshots.Bookmarks.Items.Count == 1);
            Press(window, PhysicalKey.Enter);
            var obj = snapshots.Navigator.Current.ShouldBeOfType<ObjectViewModel>();
            await obj.LoadTask;
            Press(window, PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
            snapshots.Navigator.Current.ShouldBe(instances);

            // The path survives other pages and tabs; Command+L focuses it and Up moves along it.
            Press(window, digits[0], command);
            Press(window, digits[(int)SnapshotTabs.Retainers], RawInputModifiers.Alt);
            Press(window, digits[(int)SnapshotTabs.Snapshot], RawInputModifiers.Alt);
            snapshots.Navigator.Current.ShouldBe(instances);
            Press(window, PhysicalKey.L, command);
            Dispatcher.UIThread.RunJobs();
            var focused = window.FocusManager!.GetFocusedElement() as Visual;
            focused.ShouldNotBeNull().FindAncestorOfType<ListBox>(includeSelf: true).ShouldNotBeNull().Name.ShouldBe("PathList");
            Press(window, PhysicalKey.ArrowUp);
            snapshots.Navigator.Current.ShouldBeOfType<TypeSummaryViewModel>();

            // Export the view's grid, refresh a page, and the shortcuts list.
            Press(window, PhysicalKey.E, command);
            await WaitFor(() => File.Exists(interaction.SaveFile));
            Press(window, PhysicalKey.F1);
            shell.SelectedItem.Page.ShouldBe(shell.About);
            window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "ShortcutList").ItemCount.ShouldBe(shell.ShortcutList.Count);
            Press(window, PhysicalKey.F5);

            ui.BindingErrors.ShouldBeEmpty();
            window.Close();
        });
    }

    [Fact]
    public async Task Types_selected_by_keyboard_open_as_a_set_whose_views_bind_cleanly()
    {
        // Sprint 3.1: Shift+Down selects two types, Ctrl+Shift+O (Cmd on macOS) opens them as a set; its views by keyboard.
        await ui.RunAsync(async () =>
        {
            var (shell, worker) = Shell();
            worker.Service.Rows.Add(["App.Order", "2", "80"]);
            worker.Service.Rows.Add(["App.Customer", "1", "60"]);
            worker.Service.InstanceRows.Add(["0x1000", "App.Order", "40", "Gen2", ""]);
            worker.Service.SetDominatorRows.Add((0x1000, "App.Order", 40, 500, 1));
            var snapshots = shell.Snapshots;
            ui.ClearBindingErrors();
            var window = new MainWindow(shell);
            window.Show();
            shell.SelectedItem = shell.NavigationItems.Single(i => i.Page == snapshots);
            var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            var dump = Path.Combine(_folder, "app.dmp");
            await File.WriteAllBytesAsync(dump, [1]);
            await snapshots.OpenPathAsync(dump);
            await snapshots.Types.QueryTask;
            await Settle(snapshots.Types.Rows);

            var types = Grid(window, "TypesGrid");
            types.Focus();
            Press(window, PhysicalKey.ArrowDown);
            Press(window, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
            types.SelectedItems.Count.ShouldBe(2);
            Press(window, PhysicalKey.O, command | RawInputModifiers.Shift);

            var set = snapshots.Navigator.Current.ShouldBeOfType<SetViewModel>();
            set.Title.ShouldBe("2 types");
            await set.Types.QueryTask;
            await Settle(set.Types.Rows);
            Grid(window, "TypesGrid").DataContext.ShouldBeSameAs(set.Types);

            var views = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ViewList" && l.IsEffectivelyVisible);
            views.ContainerFromIndex(views.SelectedIndex).ShouldNotBeNull().Focus(NavigationMethod.Tab);
            Press(window, PhysicalKey.ArrowRight);
            set.View.ShouldBe(SetViews.Instances);
            await set.Instances!.QueryTask;
            await Settle(set.Instances.Rows);
            Press(window, PhysicalKey.ArrowRight);
            set.View.ShouldBe(SetViews.Generations);
            Press(window, PhysicalKey.ArrowRight);
            set.View.ShouldBe(SetViews.Dominators);
            await set.Dominators!.QueryTask;
            await Settle(set.Dominators.Rows);
            Grid(window, "SetDominatorsGrid").IsEffectivelyVisible.ShouldBeTrue();

            ui.BindingErrors.ShouldBeEmpty();
            window.Close();
        });
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(4, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "condition not met in time");
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    internal static DataGrid Grid(Window window, string name)
    {
        // A set's Types view (sprint 3.1) has a second types grid: the shown one is meant when both exist.
        var grids = window.GetVisualDescendants().OfType<DataGrid>().Where(g => g.Name == name).ToList();
        return grids.Count == 1 ? grids[0] : grids.Single(g => g.IsEffectivelyVisible);
    }

    // Lets the first page of rows arrive and the grids lay out with it.
    internal static async Task Settle<TRow>(VirtualRowList<TRow> rows)
        where TRow : class, IIndexedRow
    {
        Dispatcher.UIThread.RunJobs();
        await rows.WhenLoadedAsync();
        Dispatcher.UIThread.RunJobs();
    }

    internal (ShellViewModel Shell, FakeWorkerClient Worker) Shell(string? unavailable = null, FakeUserInteraction? interaction = null)
    {
        // The app's own view model wiring over fakes (sprint 2.3.7 review M15).
        var worker = new FakeWorkerClient();
        var jobs = new JobTracker(TimeProvider.System);
        var capture = new FakeCaptureService(Path.Combine(_folder, "sessions"), jobs) { UnavailableReason = unavailable };
        var services = new ServiceCollection()
            .AddSingleton(jobs)
            .AddSingleton<IWorkerClient>(worker)
            .AddSingleton(new WorkerOperations(worker, jobs))
            .AddSingleton(new SnapshotSession(worker, jobs))
            .AddSingleton<Application.Sessions.ICaptureService>(capture)
            .AddSingleton(new MonitorService(capture, new SyntheticCounterSource(0, perSecond: 1)))
            .AddSingleton(new Application.Sessions.SessionLibrary(Path.Combine(_folder, "sessions")))
            .AddSingleton(sp => new Application.Allocations.AllocationSession(worker, jobs, new Application.Sessions.TraceService(), sp.GetRequiredService<Application.Sessions.SessionLibrary>()))
            .AddSingleton<IUiDispatcher>(ImmediateUiDispatcher.Instance)
            .AddSingleton<IUserInteraction>(interaction ?? new FakeUserInteraction())
            .AddHeapScopeViewModels()
            .BuildServiceProvider();
        return (services.GetRequiredService<ShellViewModel>(), worker);
    }
}
