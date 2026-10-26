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

            tree.SelectedIndex = 0;
            tree.Focus();
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

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static DataGrid Grid(Window window, string name) =>
        window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == name);

    // Lets the first page of rows arrive and the grids lay out with it.
    private static async Task Settle<TRow>(VirtualRowList<TRow> rows)
        where TRow : class, IIndexedRow
    {
        Dispatcher.UIThread.RunJobs();
        await rows.WhenLoadedAsync();
        Dispatcher.UIThread.RunJobs();
    }

    private (ShellViewModel Shell, FakeWorkerClient Worker) Shell(string? unavailable = null)
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
            .AddSingleton<IUiDispatcher>(ImmediateUiDispatcher.Instance)
            .AddSingleton<IUserInteraction>(new FakeUserInteraction())
            .AddHeapScopeViewModels()
            .BuildServiceProvider();
        return (services.GetRequiredService<ShellViewModel>(), worker);
    }
}
