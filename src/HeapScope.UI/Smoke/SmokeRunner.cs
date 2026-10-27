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


using HeapScope.Contracts;
using Avalonia.Threading;
using HeapScope.UI.Services;
using HeapScope.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.Smoke;

/// <summary>
/// The unattended smoke runs (`--smoke-synthetic`, `--smoke-open`, `--smoke-live`, ARCHITECTURE.md 8): each drives the
/// real window through its view models, checks the dispatcher budget and returns the process exit code (0 ok,
/// <see cref="SmokeLagExceeded"/>, <see cref="SmokeJobFailed"/>). Moved out of <see cref="App"/> in sprint 1.9.
/// </summary>
internal sealed class SmokeRunner(IServiceProvider services)
{
    /// <summary>Smoke mode exit code: the dispatcher was blocked longer than <see cref="SmokeLagLimit"/>.</summary>
    public const int SmokeLagExceeded = 3;

    /// <summary>Smoke mode exit code: the run did not complete.</summary>
    public const int SmokeJobFailed = 4;

    /// <summary>
    /// Regression gate of the smoke run: twice the 100 ms budget. Breaches of the budget itself are logged by
    /// <see cref="DispatcherWatchdog"/>; see SPRINTS.md sprint 0.3 decision 10.
    /// </summary>
    public static readonly TimeSpan SmokeLagLimit = DispatcherWatchdog.Budget * 2;

    private readonly IServiceProvider _services = services;

    /// <summary>Smoke mode exit code: the main window took longer than <see cref="FirstWindowLimit"/> to show.</summary>
    public const int SmokeColdStartExceeded = 5;

    /// <summary>
    /// Gross regression check of the cold start (sprint 2.3.6, decision 8): process start to the main window's first frame.
    /// Measured 2.2 s warm for a Release build on the development machine (WPF the same), and 8 to 15 s for the first launch
    /// after a build while its files are scanned, so the limit only catches a start that hangs. Not checked headless.
    /// </summary>
    public static readonly TimeSpan FirstWindowLimit = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Smoke mode exit code: a grid walked a whole row list (<see cref="Controls.VirtualCollectionViews.Enumerations"/>),
    /// which makes a row per index (sprint 2.3.6 review).
    /// </summary>
    public const int SmokeGridEnumerated = 6;

    /// <summary>The exit code of the smoke run <paramref name="options"/> ask for, or null when they ask for none.</summary>
    public async Task<int?> RunAsync(UiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        int? result = options switch
        {
            { SmokeSyntheticObjects: int objects } => await RunSmokeAsync(objects),
            { SmokeOpenPath: { } dump } => await RunSmokeOpenAsync(dump),
            { SmokeLiveSamples: int samples } => await RunSmokeLiveAsync(samples),
            _ => null,
        };
        if (result is null)
        {
            return null;
        }

        // Every smoke run reports the UI's memory and proves on real pages and dumps that no grid walked its whole list.
        var enumerations = Controls.VirtualCollectionViews.Enumerations;
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        {
            _services.GetRequiredService<ILogger<SmokeRunner>>().LogInformation(
                "Smoke memory: UI private {PrivateMb:N0} MB, managed heap {ManagedMb:N0} MB; whole-list grid enumerations {Enumerations}",
                process.PrivateMemorySize64 / (1024.0 * 1024),
                GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024),
                enumerations);
        }

        return SmokeVerdict.Final(result.Value, enumerations, Program.IsHeadless, App.FirstWindowTime);
    }

    // Waits until the UI thread has nothing left to do, rendering included.
    private static Task IdleAsync() => Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).GetTask();

    /// <summary>Runs one synthetic job, checks the dispatcher lag budget and closes the app with a result code.</summary>
    private async Task<int> RunSmokeAsync(int objects)
    {
        var services = _services;
        var logger = services.GetRequiredService<ILogger<SmokeRunner>>();
        var watchdog = services.GetRequiredService<DispatcherWatchdog>();
        var home = services.GetRequiredService<HomeViewModel>();

        // Measure steady state, not one-time cold costs (first window render, first job row template, JIT):
        // run a tiny warm-up job, then wait until the dispatcher is idle. Cold-start lag is tracked separately
        // (SPRINTS.md backlog: UI cold start).
        home.ObjectCount = 10_000;
        await home.RunSyntheticCommand.ExecuteAsync(null);
        await IdleAsync();
        await Task.Delay(500);
        home.ObjectCount = objects;
        watchdog.Reset();
        await home.RunSyntheticCommand.ExecuteAsync(null);
        var completed = home.LastJob is { Result.IsCompletedSuccessfully: true };

        // Let queued progress updates drain so their cost is measured too.
        await Task.Delay(500);
        var lag = watchdog.MaxLag;
        var operation = watchdog.MaxOperationBound;
        logger.LogInformation(
            "Smoke run: job completed {Completed}, maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation at most {OperationMs:N0} ms",
            completed,
            lag.TotalMilliseconds,
            operation.TotalMilliseconds);

        // The budget's subject (a single dispatcher work item, its upper bound since sprint 2.3.6) is checked hard at
        // 100 ms; probe lag, which also contains OS scheduling noise, is checked at the 2x gate (sprint 0.3 decision 10).
        return SmokeVerdict.Phase(completed, operation, lag);
    }

    /// <summary>
    /// Charts <paramref name="count"/> made-up samples on the Live page at 200 a second (sprint 1.7): the page's first
    /// show is logged as a cold cost; the feed, every sample a redraw of up to 30 minutes of history, is checked.
    /// </summary>
    private async Task<int> RunSmokeLiveAsync(int count)
    {
        var services = _services;
        var logger = services.GetRequiredService<ILogger<SmokeRunner>>();
        var watchdog = services.GetRequiredService<DispatcherWatchdog>();
        var shell = services.GetRequiredService<ShellViewModel>();
        var live = shell.Live ?? throw new InvalidOperationException("The Live page is not registered.");

        shell.SelectedItem = shell.NavigationItems.Single(i => i.Page == live);
        await IdleAsync();
        await Task.Delay(500);
        logger.LogInformation(
            "Smoke live warm-up: Live page first shown (cold): maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation at most {OperationMs:N0} ms",
            watchdog.MaxLag.TotalMilliseconds,
            watchdog.MaxOperationBound.TotalMilliseconds);
        watchdog.Reset();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        live.SelectedProcess = new ProcessRow(Environment.ProcessId, "synthetic", string.Empty, string.Empty, 0, string.Empty, false);
        live.WriteTimeline = false;
        live.StartCommand.Execute(null);
        if (live.Run is { } run)
        {
            await run.Completion;
        }

        await IdleAsync();
        await Task.Delay(500);
        var completed = live.Samples.Count == Math.Min(count, LiveViewModel.WindowSamples) && !live.IsRunning;
        var (lag, operation) = (watchdog.MaxLag, watchdog.MaxOperationBound);
        logger.LogInformation(
            "Smoke live: {Count:N0} samples in {Seconds:N1} s, {Kept:N0} charted ({Status}), maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation at most {OperationMs:N0} ms",
            count,
            stopwatch.Elapsed.TotalSeconds,
            live.Samples.Count,
            live.Status ?? "ok",
            lag.TotalMilliseconds,
            operation.TotalMilliseconds);
        return SmokeVerdict.Phase(completed, operation, lag);
    }

    /// <summary>
    /// Opens a dump through the Snapshots page, waits for its overview, checks the dispatcher budget like
    /// <see cref="RunSmokeAsync"/> and closes the app with a result code (sprint 1.2).
    /// </summary>
    private async Task<int> RunSmokeOpenAsync(string dump)
    {
        var services = _services;
        var logger = services.GetRequiredService<ILogger<SmokeRunner>>();
        var watchdog = services.GetRequiredService<DispatcherWatchdog>();
        var home = services.GetRequiredService<HomeViewModel>();
        var snapshots = services.GetRequiredService<SnapshotsViewModel>();

        // Warm-up as in RunSmokeAsync, so one-time cold costs are not counted: a tiny job, then the Snapshots page shown
        // once (pages are built on first show and kept, sprint 1.5). The cold first show is logged, not checked.
        home.ObjectCount = 10_000;
        await home.RunSyntheticCommand.ExecuteAsync(null);
        await IdleAsync();
        await Task.Delay(500);
        watchdog.Reset();
        var shell = services.GetRequiredService<ShellViewModel>();
        shell.SelectedItem = shell.NavigationItems.Single(i => i.Page == snapshots);
        await IdleAsync();
        await Task.Delay(500);
        logger.LogInformation(
            "Smoke warm-up: Snapshots page first shown (cold): maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation at most {OperationMs:N0} ms",
            watchdog.MaxLag.TotalMilliseconds,
            watchdog.MaxOperationBound.TotalMilliseconds);
        watchdog.Reset();

        // --smoke-trace records this process from here to the end of the run (sprint 1.5), for diagnosing lags.
        using var traceStop = new CancellationTokenSource();
        var trace = services.GetRequiredService<UiOptions>().SmokeTracePath is { } tracePath
            ? services.GetRequiredService<HeapScope.Application.Sessions.TraceService>()
                .RecordAsync(Environment.ProcessId, tracePath, HeapScope.Application.Sessions.TraceKind.Cpu, TimeSpan.FromMinutes(10), traceStop.Token)
            : null;

        // The trace stops however the run ends: a failing step must not leave it recording (sprint 1.5 review).
        VirtualRowList<TypeRowItem> rows;
        bool completed;
        TimeSpan lag;
        TimeSpan operation;
        try
        {
            await snapshots.OpenPathAsync(dump);
            await snapshots.Types.QueryTask;

            // Touch the first rows as the grid would, and wait for their page.
            rows = snapshots.Types.Rows;
            await TouchRowsAsync(rows);
            completed = snapshots.Selected is not null && rows.Count > 0 && rows.GetRow(0).IsLoaded;

            // The largest types as shown, for checks of the known answers in CI (sprint 2.3.6). A contract: ci.yml matches
            // "Smoke open types: ... <type> <count>"; keep the wording (sprint 2.3.7 review M11).
            logger.LogInformation(
                "Smoke open types: {Types}",
                string.Join("; ", Enumerable.Range(0, Math.Min(rows.Count, 50)).Select(rows.GetRow).Where(r => r.IsLoaded).Select(r => $"{r.Name} {r.Objects}")));
            await Task.Delay(500);

            // The phases below reset the watchdog after their cold first show, so the checked maxima are combined here: the
            // open and the types grid, then each phase's warm part.
            lag = watchdog.MaxLag;
            operation = watchdog.MaxOperationBound;
            logger.LogInformation(
                "Smoke open phase: maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation at most {OperationMs:N0} ms",
                lag.TotalMilliseconds,
                operation.TotalMilliseconds);
            void Combine()
            {
                lag = lag > watchdog.MaxLag ? lag : watchdog.MaxLag;
                operation = operation > watchdog.MaxOperationBound ? operation : watchdog.MaxOperationBound;
            }

            if (completed)
            {
                completed = await RunSmokeDrillAsync(snapshots, watchdog, logger);
                await Task.Delay(500);
                Combine();
            }

            if (completed)
            {
                completed = await RunSmokeInspectionsAsync(snapshots, watchdog, logger);
                await Task.Delay(500);
                Combine();
            }

            if (completed)
            {
                completed = await RunSmokeRetainersAsync(snapshots, watchdog, logger);
                await Task.Delay(500);
                Combine();
            }

            if (completed && services.GetRequiredService<UiOptions>().SmokeAllocationsPath is { } allocationTrace)
            {
                completed = await RunSmokeAllocationsAsync(shell, allocationTrace, watchdog, logger);
                shell.SelectedItem = shell.NavigationItems.Single(i => i.Page == snapshots);
                await Task.Delay(500);
                Combine();
            }

            if (completed && services.GetRequiredService<UiOptions>().SmokeComparePath is { } other)
            {
                completed = await RunSmokeCompareAsync(snapshots, other, watchdog, logger);
                await Task.Delay(500);
                Combine();
            }
        }
        finally
        {
            if (trace is not null)
            {
                await traceStop.CancelAsync();
                try
                {
                    var recorded = await trace;
                    logger.LogInformation("Smoke trace written: {Path} ({Bytes:N0} bytes)", recorded.Path, recorded.Bytes);
                }
                catch (Exception ex) when (ex is HeapScope.Application.Sessions.CaptureFailedException or OperationCanceledException)
                {
                    logger.LogWarning(ex, "Smoke trace failed");
                }
            }
        }

        logger.LogInformation(
            "Smoke open: snapshot shown {Completed} ({Status}), {Pages} of {TotalPages} row pages fetched, maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation at most {OperationMs:N0} ms",
            completed,
            snapshots.Status ?? "ok",
            rows.RequestedPages.Count,
            (rows.Count + VirtualRowList.PageSize - 1) / VirtualRowList.PageSize,
            lag.TotalMilliseconds,
            operation.TotalMilliseconds);
        return SmokeVerdict.Phase(completed, operation, lag);
    }

    /// <summary>
    /// Drills down as a user would (sprint 1.5): the largest type's objects, the largest object, its references. The first
    /// showing of each view is logged as a cold cost; a re-sort of the objects and a second object are the checked part.
    /// Returns whether every step showed its data.
    /// </summary>
    private static async Task<bool> RunSmokeDrillAsync(SnapshotsViewModel snapshots, DispatcherWatchdog watchdog, ILogger logger)
    {
        var navigator = snapshots.Navigator;
        var type = snapshots.Types.Rows.GetRow(0);
        snapshots.Types.OpenRowCommand!.Execute(type);
        if (navigator.Current is not InstancesViewModel instances)
        {
            return Failed(logger, "the largest type's objects did not open");
        }

        await instances.QueryTask;
        var objects = instances.Rows;
        await TouchRowsAsync(objects);
        if (objects.Count == 0)
        {
            return Failed(logger, "the largest type has no objects");
        }

        instances.OpenRowCommand!.Execute(objects.GetRow(0));
        if (navigator.Current is not ObjectViewModel first)
        {
            return Failed(logger, "the first object did not open");
        }

        await first.LoadTask;
        await first.ReferenceRows.WhenLoadedAsync();

        // Sprint 2.1, cold: the object's referrers (the first query of a snapshot builds its reverse index in the worker
        // while the window stays responsive) and who references the type's objects, each view shown for the first time.
        first.ReferencesView = ObjectReferenceTabs.Incoming;
        await first.IncomingTask;
        await first.IncomingRows.WhenLoadedAsync();

        // Sprint 2.2, cold: why the object is alive, and why the type's objects are. Sprint 2.3, cold: its retained size,
        // after the paths (the first such query computes the snapshot's dominators, or opens them from their sidecar).
        first.ReferencesView = ObjectReferenceTabs.Retention;
        await first.RetentionTask;
        await first.RetainedTask;
        navigator.BackCommand.Execute(null);
        instances.WhyAliveCommand!.Execute(null);
        if (navigator.Current is not TypePathsViewModel typePaths)
        {
            return Failed(logger, "the type's retention paths did not open");
        }

        await typePaths.LoadTask;
        var typePathsShown = typePaths.Totals is not null;
        navigator.BackCommand.Execute(null);
        instances.ReferencedByCommand!.Execute(null);
        if (navigator.Current is not TypeReferrersViewModel referrers)
        {
            return Failed(logger, "the type's referrers did not open");
        }

        await referrers.QueryTask;
        await referrers.Rows.WhenLoadedAsync();

        // Opening the next object drops the referrers from the forward history (their rows are released), so judge them now.
        var referrersShown = referrers.Totals is not null;
        await IdleAsync();
        logger.LogInformation(
            "Smoke drill: {Type} has {Objects:N0} objects; first object {Object} ({Status}), {Incoming}, {Retention}, {Retained}; {Title}: {Totals}; {PathsTitle}: {Paths} key paths; first show (cold): maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation at most {OperationMs:N0} ms",
            instances.TypeName,
            objects.Count,
            first.Title,
            first.Status ?? first.FieldsUnavailable ?? "fields read",
            first.IncomingTotals ?? first.IncomingStatus,
            first.RetentionStatus,
            first.RetainedSummary,
            referrers.Title,
            referrers.Totals ?? referrers.Status,
            typePaths.Title,
            typePaths.Paths.Count,
            watchdog.MaxLag.TotalMilliseconds,
            watchdog.MaxOperationBound.TotalMilliseconds);

        // Checked part: back to the objects, a re-sort, retained sizes (sprint 2.3, the dominators warm), and another object
        // with its referrers and retention (the views and the indices warm).
        watchdog.Reset();
        navigator.BackCommand.Execute(null);
        instances.SortByCommand.Execute(Contracts.InstanceColumns.Address);
        await instances.QueryTask;
        instances.ShowRetained = true;
        await instances.QueryTask;
        var retainedShown = instances.RetainedColumnShown;
        objects = instances.Rows;
        await TouchRowsAsync(objects);
        instances.OpenRowCommand!.Execute(objects.GetRow(objects.Count > 1 ? 1 : 0));
        if (navigator.Current is not ObjectViewModel second)
        {
            return Failed(logger, "the second object did not open");
        }

        await second.LoadTask;
        await second.ReferenceRows.WhenLoadedAsync();
        second.ReferencesView = ObjectReferenceTabs.Incoming;
        await second.IncomingTask;
        await second.IncomingRows.WhenLoadedAsync();
        second.ReferencesView = ObjectReferenceTabs.Retention;
        await second.RetentionTask;
        await second.RetainedTask;
        // Each check by name, so a red run says which view did not show its data (sprint 2.3.7 review M11).
        (string Check, bool Passed)[] checks =
        [
            ("first object read", first.Status is null),
            ("second object read", second.Status is null),
            ("first object's fields or text", first.Fields.Count > 0 || first.FieldsUnavailable is not null || first.Text is not null),
            ("first object's referrers", first.IncomingTotals is not null),
            ("second object's referrers", second.IncomingTotals is not null),
            ("type's referrers", referrersShown),
            ("first object's retention tree", first.RetentionTree.Count == 1),
            ("second object's retention tree", second.RetentionTree.Count == 1),
            ("type's retention paths", typePathsShown),
            ("retained column", retainedShown),
            ("first object's retained size", first.RetainedSummary?.StartsWith("Retains", StringComparison.Ordinal) == true),
            ("second object's retained size", second.RetainedSummary?.StartsWith("Retains", StringComparison.Ordinal) == true),
        ];
        var failed = checks.Where(c => !c.Passed).Select(c => c.Check).ToArray();
        return failed.Length == 0 || Failed(logger, string.Join(", ", failed));
    }

    private static bool Failed(ILogger logger, string step)
    {
        logger.LogWarning("Smoke step failed: {Step}", step);
        return false;
    }

    /// <summary>Asks for the first rows as a grid on screen would, and waits for their page.</summary>
    private static async Task TouchRowsAsync<TRow>(VirtualRowList<TRow> rows)
        where TRow : class, IIndexedRow
    {
        for (var i = 0; i < Math.Min(rows.Count, 50); i++)
        {
            rows.GetRow(i);
        }

        await rows.WhenLoadedAsync();
    }

    /// <summary>
    /// Shows the Inspections tab (sprint 1.6): every inspection runs in the worker while the tab stays responsive; the
    /// rows of the selected one are fetched as the grid would. Returns whether every available inspection finished.
    /// </summary>
    private static async Task<bool> RunSmokeInspectionsAsync(SnapshotsViewModel snapshots, DispatcherWatchdog watchdog, ILogger logger)
    {
        watchdog.Reset();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        snapshots.Tab = SnapshotTabs.Inspections;
        var inspections = snapshots.Inspections;
        await inspections.RunTask;
        var rows = inspections.Rows;
        await TouchRowsAsync(rows);
        await IdleAsync();
        var finished = inspections.Tiles.Count > 0 && inspections.Tiles.All(t => !t.IsAvailable || (t.Run is not null && t.Status is null));
        logger.LogInformation(
            "Smoke inspections: {Count} inspections in {Seconds:N1} s ({Findings}), selected {Selected}; maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation at most {OperationMs:N0} ms",
            inspections.Tiles.Count,
            stopwatch.Elapsed.TotalSeconds,
            string.Join("; ", inspections.Tiles.Select(t => $"{t.Info.Name}: {t.Status ?? t.Summary}")),
            inspections.Selected?.Title ?? "none",
            watchdog.MaxLag.TotalMilliseconds,
            watchdog.MaxOperationBound.TotalMilliseconds);
        snapshots.Tab = SnapshotTabs.Snapshot;
        return finished;
    }

    /// <summary>
    /// Shows the Retainers tab (sprint 2.4): the biggest retainers and their icicle (the first time computes or opens the
    /// dominators in the worker; logged, not checked), then the checked part in the warm tab: expand the first row and focus
    /// on it, as a user would. Returns whether the tree showed rows each time.
    /// </summary>
    private static async Task<bool> RunSmokeRetainersAsync(SnapshotsViewModel snapshots, DispatcherWatchdog watchdog, ILogger logger)
    {
        var retainers = snapshots.Retainers;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        snapshots.Tab = SnapshotTabs.Retainers;
        await retainers.QueryTask;
        await retainers.IcicleTask;
        await TouchRowsAsync(retainers.Rows);
        await IdleAsync();
        var shown = retainers.Rows.Count > 0 && retainers.Rows.GetRow(0).IsLoaded;
        logger.LogInformation(
            "Smoke retainers: {Shown} ({Status}) in {Seconds:N1} s, {Rows:N0} rows, {Bars:N0} icicle bars, first {First}; first show (cold): maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation at most {OperationMs:N0} ms",
            shown,
            retainers.Status ?? retainers.IcicleStatus ?? "ok",
            stopwatch.Elapsed.TotalSeconds,
            retainers.Rows.Count,
            retainers.IcicleSegments.Count,
            shown ? retainers.Rows.GetRow(0).Name : "none",
            watchdog.MaxLag.TotalMilliseconds,
            watchdog.MaxOperationBound.TotalMilliseconds);
        if (!shown)
        {
            return false;
        }

        watchdog.Reset();
        var first = retainers.Rows.GetRow(0);
        retainers.ToggleCommand.Execute(first);
        await retainers.QueryTask;
        await TouchRowsAsync(retainers.Rows);
        var expanded = retainers.Rows.Count;
        retainers.FocusCommand.Execute(retainers.Rows.GetRow(0));
        await retainers.QueryTask;
        await retainers.IcicleTask;
        await TouchRowsAsync(retainers.Rows);
        await IdleAsync();
        var focused = retainers.HasFocus && retainers.Rows.Count > 0 && retainers.Rows.GetRow(0).IsLoaded;
        logger.LogInformation(
            "Smoke retainers drill: expanded to {Expanded:N0} rows, focused {Focused} ({Totals}); maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation at most {OperationMs:N0} ms",
            expanded,
            focused,
            retainers.Totals,
            watchdog.MaxLag.TotalMilliseconds,
            watchdog.MaxOperationBound.TotalMilliseconds);
        retainers.ShowTopCommand.Execute(null);
        await retainers.QueryTask;
        snapshots.Tab = SnapshotTabs.Snapshot;
        return focused;
    }

    /// <summary>
    /// Shows an allocations trace on the Allocations page (sprint 2.5): its first show is logged as a cold cost; expanding
    /// the first back trace and switching to the call tree are the checked part. Returns whether both showed rows.
    /// </summary>
    private static async Task<bool> RunSmokeAllocationsAsync(ShellViewModel shell, string path, DispatcherWatchdog watchdog, ILogger logger)
    {
        var allocations = shell.Allocations;
        if (allocations is null)
        {
            return false;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await allocations.OpenPathAsync(path);
        if (allocations.Selected is not { } trace)
        {
            logger.LogWarning("Smoke allocations: {Path} was not shown: {Status}", path, allocations.Status);
            return false;
        }

        await trace.LoadTask;
        await trace.Types.QueryTask;
        await trace.Tree.QueryTask;
        await TouchRowsAsync(trace.Types.Rows);
        await TouchRowsAsync(trace.Tree.Rows);
        await IdleAsync();
        var shown = trace.Tree.Rows.Count > 0 && trace.Tree.Rows.GetRow(0).IsLoaded;
        logger.LogInformation(
            "Smoke allocations: {Shown} ({Status}) in {Seconds:N1} s, {Types:N0} types, {Stacks} top stacks, first back trace {First}; first show (cold): maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation at most {OperationMs:N0} ms",
            shown,
            trace.Tree.Status ?? trace.StacksStatus ?? "ok",
            stopwatch.Elapsed.TotalSeconds,
            trace.Types.Rows.Count,
            trace.Stacks.Count,
            shown ? trace.Tree.Rows.GetRow(0).Name : "none",
            watchdog.MaxLag.TotalMilliseconds,
            watchdog.MaxOperationBound.TotalMilliseconds);
        if (!shown)
        {
            return false;
        }

        watchdog.Reset();
        trace.Tree.ToggleCommand.Execute(trace.Tree.Rows.GetRow(0));
        await trace.Tree.QueryTask;
        await TouchRowsAsync(trace.Tree.Rows);
        var expanded = trace.Tree.Rows.Count;
        trace.ShowCallTree = true;
        await trace.Tree.QueryTask;
        await TouchRowsAsync(trace.Tree.Rows);
        await IdleAsync();
        var callTree = trace.Tree.Rows.Count > 0 && trace.Tree.Rows.GetRow(0).IsLoaded;
        logger.LogInformation(
            "Smoke allocations drill: expanded to {Expanded:N0} rows, call tree {CallTree}; maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation at most {OperationMs:N0} ms",
            expanded,
            callTree,
            watchdog.MaxLag.TotalMilliseconds,
            watchdog.MaxOperationBound.TotalMilliseconds);
        return callTree;
    }

    /// <summary>
    /// Opens a second dump and shows the comparison (sprint 1.4): the pickers fill themselves with the two open snapshots,
    /// the grid's first rows are fetched as the grid would. Returns whether the comparison showed rows.
    /// </summary>
    private static async Task<bool> RunSmokeCompareAsync(SnapshotsViewModel snapshots, string dump, DispatcherWatchdog watchdog, ILogger logger)
    {
        // Show the (empty) tab first and let it render, then open: the tab's first layout and the rows are logged apart.
        snapshots.Tab = SnapshotTabs.Compare;
        await IdleAsync();
        logger.LogInformation("Smoke compare: tab shown");
        await snapshots.OpenPathAsync(dump);
        await snapshots.Compare.QueryTask;
        var rows = snapshots.Compare.Rows;
        await TouchRowsAsync(rows);
        var shown = rows.Count > 0 && rows.GetRow(0).IsLoaded;
        logger.LogInformation(
            "Smoke compare: {Shown} ({Status}), {Rows:N0} rows, first {First}; {Totals}; first show (cold): maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation at most {OperationMs:N0} ms",
            shown,
            snapshots.Compare.Status ?? snapshots.Status ?? "ok",
            rows.Count,
            shown ? rows.GetRow(0).Name : "none",
            snapshots.Compare.Totals,
            watchdog.MaxLag.TotalMilliseconds,
            watchdog.MaxOperationBound.TotalMilliseconds);

        // The checked part, like the other smoke runs after their warm-up: a re-query and its rows in the warm grid
        // (one-time cold costs of the tab are logged above and tracked by the UI cold start backlog item).
        await IdleAsync();
        watchdog.Reset();
        snapshots.Compare.SortByCommand.Execute(TypeDiffColumns.ObjectsDelta);
        await snapshots.Compare.QueryTask;
        rows = snapshots.Compare.Rows;
        await TouchRowsAsync(rows);
        return shown && rows.Count > 0 && rows.GetRow(0).IsLoaded;
    }
}
