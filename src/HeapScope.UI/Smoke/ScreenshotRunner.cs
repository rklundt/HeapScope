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

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using HeapScope.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.Smoke;

/// <summary>
/// Captures the pages of the app as PNG files, in the light and the dark theme, for the design review and the README
/// (<c>--headless --screenshots folder --smoke-open dump</c>, optionally <c>--smoke-compare</c>, <c>--smoke-allocations</c>
/// and <c>--smoke-live</c>). Fixture dumps only: everything a page shows goes into a picture. The Processes page's rows are
/// made up, so this machine's real processes and command lines never appear.
/// </summary>
internal sealed class ScreenshotRunner(IServiceProvider services, Window window, string folder)
{
    private readonly ILogger _logger = services.GetRequiredService<ILogger<ScreenshotRunner>>();

    /// <summary>Waits until the UI thread has nothing left to do, then a little longer for queued rows and charts.</summary>
    private static async Task SettleAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).GetTask();
        await Task.Delay(400);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).GetTask();
    }

    /// <summary>Saves the window as it stands, once per theme.</summary>
    private async Task CaptureAsync(string name)
    {
        var app = Avalonia.Application.Current!;
        foreach (var (theme, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            app.RequestedThemeVariant = theme;
            await SettleAsync();
            var path = Path.Combine(folder, $"{name}-{suffix}.png");
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered.");
            frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            _logger.LogInformation("Screenshot written: {Path}", path);
        }
    }

    /// <summary>Drives the pages and returns the exit code.</summary>
    public async Task<int> RunAsync(UiOptions options)
    {
        Directory.CreateDirectory(folder);
        window.Width = 1440;
        window.Height = 900;
        var shell = services.GetRequiredService<ShellViewModel>();
        var snapshots = services.GetRequiredService<SnapshotsViewModel>();
        void Show(object page) => shell.SelectedItem = shell.NavigationItems.Single(i => i.Page == page);

        await SettleAsync();
        await CaptureAsync("01-home");

        var processes = services.GetRequiredService<ProcessesViewModel>();
        Show(processes);
        {
            await SettleAsync();
            await Task.Delay(1500);
            processes.Processes.Clear();
            processes.Processes.Add(new ProcessRow(18244, "LeakyApi", ".NET 10.0.14", "No", 412_000_000, "LeakyApi.exe --urls http://localhost:5100", false));
            processes.Processes.Add(new ProcessRow(20112, "OrdersWorker", ".NET 8.0.21", "No", 1_260_000_000, "dotnet OrdersWorker.dll", false));
            processes.Processes.Add(new ProcessRow(9320, "ReportingService", ".NET 9.0.10", "Yes", 788_000_000, "ReportingService.exe", true));
            await CaptureAsync("02-processes");
        }

        if (options.SmokeOpenPath is { } dump)
        {
            Show(snapshots);
            await SettleAsync();
            await snapshots.OpenPathAsync(dump);
            await snapshots.Types.QueryTask;
            await TouchAsync(snapshots.Types.Rows);
            await CaptureAsync("03-types");

            var navigator = snapshots.Navigator;
            snapshots.Types.OpenRowCommand!.Execute(snapshots.Types.Rows.GetRow(0));
            var instances = (InstancesViewModel)navigator.Current!;
            await instances.QueryTask;
            await TouchAsync(instances.Rows);
            await CaptureAsync("04-instances");

            instances.OpenRowCommand!.Execute(instances.Rows.GetRow(0));
            var item = (ObjectViewModel)navigator.Current!;
            await item.LoadTask;
            await item.ReferenceRows.WhenLoadedAsync();
            await CaptureAsync("05-object");
            item.ReferencesView = ObjectReferenceTabs.Retention;
            await item.RetentionTask;
            await item.RetainedTask;
            await CaptureAsync("06-object-retention");

            navigator.BackCommand.Execute(null);
            instances.WhyAliveCommand!.Execute(null);
            var paths = (TypePathsViewModel)navigator.Current!;
            await paths.LoadTask;
            await CaptureAsync("07-why-alive");
            paths.ShowDiagram = true;
            await CaptureAsync("08-why-alive-diagram");
            paths.ShowDiagram = false;
            navigator.BackCommand.Execute(null);

            snapshots.Tab = SnapshotTabs.Inspections;
            await snapshots.Inspections.RunTask;
            await TouchAsync(snapshots.Inspections.Rows);
            await CaptureAsync("09-inspections");

            snapshots.Tab = SnapshotTabs.Retainers;
            await SettleAsync();
            await snapshots.Retainers.QueryTask;
            await snapshots.Retainers.IcicleTask;
            await TouchAsync(snapshots.Retainers.Rows);
            await CaptureAsync("10-retainers");

            if (options.SmokeComparePath is { } other)
            {
                snapshots.Tab = SnapshotTabs.Compare;
                await SettleAsync();
                await snapshots.OpenPathAsync(other);
                await snapshots.Compare.QueryTask;
                await TouchAsync(snapshots.Compare.Rows);
                await CaptureAsync("11-compare");
            }
        }

        if (options.SmokeAllocationsPath is { } trace && shell.Allocations is { } allocations)
        {
            Show(allocations);
            await SettleAsync();
            await allocations.OpenPathAsync(trace);
            await SettleAsync();
            await CaptureAsync("12-allocations");
        }

        if (options.SmokeLiveSamples is not null && shell.Live is { } live)
        {
            Show(live);
            await SettleAsync();
            live.SelectedProcess = new ProcessRow(18244, "LeakyApi", ".NET 10.0.14", "No", 412_000_000, string.Empty, false);
            live.WriteTimeline = false;
            live.RecordRuns = false;
            await live.StartCommand.ExecuteAsync(null);
            if (live.Run is { } run)
            {
                await run.Completion;
            }

            await CaptureAsync("13-live");
        }

        Show(shell.JobsPage);
        await CaptureAsync("14-jobs");
        Show(shell.NavigationItems.Single(i => i.Title == "About").Page);
        await CaptureAsync("15-about");
        return 0;
    }

    private static async Task TouchAsync<TRow>(VirtualRowList<TRow> rows)
        where TRow : class, IIndexedRow
    {
        for (var i = 0; i < Math.Min(rows.Count, 50); i++)
        {
            rows.GetRow(i);
        }

        await rows.WhenLoadedAsync();
    }
}
