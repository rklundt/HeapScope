using System.ComponentModel;
using System.Windows;
using HeapScope.Contracts;
using HeapScope.UI.Wpf.Services;
using HeapScope.UI.Wpf.ViewModels;
using HeapScope.UI.Wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.Wpf;

/// <summary>
/// WPF entry point and host lifecycle (ARCHITECTURE.md 8): the host starts asynchronously, and closing the main
/// window stops the host asynchronously (bounded) before the application shuts down. A stop requested by the host
/// itself closes the window through the same path.
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>Smoke mode exit code: the dispatcher was blocked longer than <see cref="SmokeLagLimit"/>.</summary>
    public const int SmokeLagExceeded = 3;

    /// <summary>
    /// Regression gate of the smoke run: twice the 100 ms budget. Breaches of the budget itself are logged by
    /// <see cref="DispatcherWatchdog"/>; see SPRINTS.md sprint 0.3 decision 10.
    /// </summary>
    public static readonly TimeSpan SmokeLagLimit = DispatcherWatchdog.Budget * 2;

    /// <summary>Smoke mode exit code: the synthetic job did not complete.</summary>
    public const int SmokeJobFailed = 4;

    private static readonly TimeSpan s_stopTimeout = TimeSpan.FromSeconds(5);

    private IHost? _host;
    private bool _stopping;
    private int _exitCode;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // An interactive tool: never let Windows move the UI to efficiency cores when the window is in the background.
        NativeMethods.DisableExecutionSpeedThrottling();

        base.OnStartup(e);
        try
        {
            _host = UiHost.Build(e.Args, Dispatcher);
            InstallLastChanceHandlers(_host.Services);
            ThemeManager.Apply(this, ThemeManager.Choose(_host.Services.GetService<Microsoft.Extensions.Configuration.IConfiguration>()?["Ui:Theme"]));
            await _host.StartAsync();

            var lifetime = _host.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Register(() => Dispatcher.BeginInvoke(() => MainWindow?.Close()));

            _host.Services.GetRequiredService<DispatcherWatchdog>();
            var window = _host.Services.GetRequiredService<MainWindow>();
            window.Closing += OnMainWindowClosing;
            window.ContentRendered += (_, _) => LogFirstRender(_host.Services);
            MainWindow = window;
            window.Show();

            var options = _host.Services.GetRequiredService<UiOptions>();
            if (options.SmokeSyntheticObjects is int objects)
            {
                await RunSmokeAsync(objects);
            }
            else if (options.SmokeOpenPath is { } dump)
            {
                await RunSmokeOpenAsync(dump);
            }
            else if (options.SmokeLiveSamples is int samples)
            {
                await RunSmokeLiveAsync(samples);
            }
        }
        catch (Exception ex)
        {
            // async void event handler: every exception must be handled here. A smoke run is unattended: no dialog, it
            // fails with its exit code and the log says why (sprint 1.5 review).
            if (e.Args.Any(a => a.StartsWith("--smoke", StringComparison.OrdinalIgnoreCase)))
            {
                _host?.Services.GetService<ILoggerFactory>()?.CreateLogger<App>().LogError(ex, "Smoke run failed");
                Shutdown(SmokeJobFailed);
                return;
            }

            MessageBox.Show($"HeapScope could not start: {ex.Message}", "HeapScope", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Logs the cold start cost: process start to first rendered window (backlog: UI cold start).</summary>
    private static void LogFirstRender(IServiceProvider services)
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var elapsed = DateTime.Now - self.StartTime;
        services.GetRequiredService<ILogger<App>>().LogInformation("First window rendered {Ms:N0} ms after process start", elapsed.TotalMilliseconds);
    }

    /// <summary>Runs one synthetic job, checks the dispatcher lag budget and closes the app with a result code.</summary>
    private async Task RunSmokeAsync(int objects)
    {
        var services = _host!.Services;
        var logger = services.GetRequiredService<ILogger<App>>();
        var watchdog = services.GetRequiredService<DispatcherWatchdog>();
        var home = services.GetRequiredService<HomeViewModel>();

        // Measure steady state, not one-time cold costs (first window render, first job row template, JIT):
        // run a tiny warm-up job, then wait until the dispatcher is idle. Cold-start lag is tracked separately
        // (SPRINTS.md backlog: UI cold start).
        home.ObjectCount = 10_000;
        await home.RunSyntheticCommand.ExecuteAsync(null);
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(500);
        home.ObjectCount = objects;
        watchdog.Reset();
        await home.RunSyntheticCommand.ExecuteAsync(null);
        var completed = home.LastJob is { Result.IsCompletedSuccessfully: true };

        // Let queued progress updates drain so their cost is measured too.
        await Task.Delay(500);
        var lag = watchdog.MaxLag;
        var operation = watchdog.MaxOperation;
        logger.LogInformation(
            "Smoke run: job completed {Completed}, maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation {OperationMs:N0} ms",
            completed,
            lag.TotalMilliseconds,
            operation.TotalMilliseconds);

        // The budget's subject (a single dispatcher work item) is checked hard at 100 ms; probe lag, which also
        // contains OS scheduling noise, is checked at the 2x gate (sprint 0.3 decision 10).
        _exitCode = !completed ? SmokeJobFailed
            : operation > DispatcherWatchdog.Budget || lag > SmokeLagLimit ? SmokeLagExceeded
            : 0;
        MainWindow?.Close();
    }

    /// <summary>
    /// Opens a dump through the Snapshots page, waits for its overview, checks the dispatcher budget like
    /// <see cref="RunSmokeAsync"/> and closes the app with a result code (sprint 1.2).
    /// </summary>
    /// <summary>
    /// Charts <paramref name="count"/> made-up samples on the Live page at 200 a second (sprint 1.7): the page's first
    /// show is logged as a cold cost; the feed, every sample a redraw of up to 30 minutes of history, is checked.
    /// </summary>
    private async Task RunSmokeLiveAsync(int count)
    {
        var services = _host!.Services;
        var logger = services.GetRequiredService<ILogger<App>>();
        var watchdog = services.GetRequiredService<DispatcherWatchdog>();
        var shell = services.GetRequiredService<ShellViewModel>();
        var live = shell.Live ?? throw new InvalidOperationException("The Live page is not registered.");

        shell.SelectedItem = shell.NavigationItems.Single(i => i.Page == live);
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(500);
        logger.LogInformation(
            "Smoke live warm-up: Live page first shown (cold): maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation {OperationMs:N0} ms",
            watchdog.MaxLag.TotalMilliseconds,
            watchdog.MaxOperation.TotalMilliseconds);
        watchdog.Reset();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        live.SelectedProcess = new ProcessRow(Environment.ProcessId, "synthetic", string.Empty, string.Empty, 0, string.Empty, false);
        live.WriteTimeline = false;
        live.StartCommand.Execute(null);
        if (live.Run is { } run)
        {
            await run.Completion;
        }

        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(500);
        var completed = live.Samples.Count == Math.Min(count, LiveViewModel.WindowSamples) && !live.IsRunning;
        var (lag, operation) = (watchdog.MaxLag, watchdog.MaxOperation);
        logger.LogInformation(
            "Smoke live: {Count:N0} samples in {Seconds:N1} s, {Kept:N0} charted ({Status}), maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation {OperationMs:N0} ms",
            count,
            stopwatch.Elapsed.TotalSeconds,
            live.Samples.Count,
            live.Status ?? "ok",
            lag.TotalMilliseconds,
            operation.TotalMilliseconds);
        _exitCode = !completed ? SmokeJobFailed
            : operation > DispatcherWatchdog.Budget || lag > SmokeLagLimit ? SmokeLagExceeded
            : 0;
        MainWindow?.Close();
    }

    private async Task RunSmokeOpenAsync(string dump)
    {
        var services = _host!.Services;
        var logger = services.GetRequiredService<ILogger<App>>();
        var watchdog = services.GetRequiredService<DispatcherWatchdog>();
        var home = services.GetRequiredService<HomeViewModel>();
        var snapshots = services.GetRequiredService<SnapshotsViewModel>();

        // Warm-up as in RunSmokeAsync, so one-time cold costs are not counted: a tiny job, then the Snapshots page shown
        // once (pages are built on first show and kept, sprint 1.5). The cold first show is logged, not checked.
        home.ObjectCount = 10_000;
        await home.RunSyntheticCommand.ExecuteAsync(null);
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(500);
        watchdog.Reset();
        var shell = services.GetRequiredService<ShellViewModel>();
        shell.SelectedItem = shell.NavigationItems.Single(i => i.Page == snapshots);
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(500);
        logger.LogInformation(
            "Smoke warm-up: Snapshots page first shown (cold): maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation {OperationMs:N0} ms",
            watchdog.MaxLag.TotalMilliseconds,
            watchdog.MaxOperation.TotalMilliseconds);
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
            for (var i = 0; i < Math.Min(rows.Count, 50); i++)
            {
                rows.GetRow(i);
            }

            await rows.WhenLoadedAsync();
            completed = snapshots.Selected is not null && rows.Count > 0 && rows.GetRow(0).IsLoaded;
            await Task.Delay(500);

            // The phases below reset the watchdog after their cold first show, so the checked maxima are combined here: the
            // open and the types grid, then each phase's warm part.
            lag = watchdog.MaxLag;
            operation = watchdog.MaxOperation;
            logger.LogInformation(
                "Smoke open phase: maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation {OperationMs:N0} ms",
                lag.TotalMilliseconds,
                operation.TotalMilliseconds);
            void Combine()
            {
                lag = lag > watchdog.MaxLag ? lag : watchdog.MaxLag;
                operation = operation > watchdog.MaxOperation ? operation : watchdog.MaxOperation;
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
            "Smoke open: snapshot shown {Completed} ({Status}), {Pages} of {TotalPages} row pages fetched, maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation {OperationMs:N0} ms",
            completed,
            snapshots.Status ?? "ok",
            rows.RequestedPages.Count,
            (rows.Count + VirtualRowList.PageSize - 1) / VirtualRowList.PageSize,
            lag.TotalMilliseconds,
            operation.TotalMilliseconds);
        _exitCode = !completed ? SmokeJobFailed
            : operation > DispatcherWatchdog.Budget || lag > SmokeLagLimit ? SmokeLagExceeded
            : 0;
        MainWindow?.Close();
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
        navigator.OpenTypeRowCommand.Execute(type);
        if (navigator.Current is not InstancesViewModel instances)
        {
            return false;
        }

        await instances.QueryTask;
        var objects = instances.Rows;
        for (var i = 0; i < Math.Min(objects.Count, 50); i++)
        {
            objects.GetRow(i);
        }

        await objects.WhenLoadedAsync();
        if (objects.Count == 0)
        {
            return false;
        }

        navigator.OpenInstanceRowCommand.Execute(objects.GetRow(0));
        if (navigator.Current is not ObjectViewModel first)
        {
            return false;
        }

        await first.LoadTask;
        await first.ReferenceRows.WhenLoadedAsync();
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        logger.LogInformation(
            "Smoke drill: {Type} has {Objects:N0} objects; first object {Object} ({Status}); first show (cold): maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation {OperationMs:N0} ms",
            instances.TypeName,
            objects.Count,
            first.Title,
            first.Status ?? first.FieldsUnavailable ?? "fields read",
            watchdog.MaxLag.TotalMilliseconds,
            watchdog.MaxOperation.TotalMilliseconds);

        // Checked part: back to the objects, a re-sort, and another object.
        watchdog.Reset();
        navigator.BackCommand.Execute(null);
        instances.SortByCommand.Execute(Contracts.InstanceColumns.Address);
        await instances.QueryTask;
        objects = instances.Rows;
        for (var i = 0; i < Math.Min(objects.Count, 50); i++)
        {
            objects.GetRow(i);
        }

        await objects.WhenLoadedAsync();
        navigator.OpenInstanceRowCommand.Execute(objects.GetRow(objects.Count > 1 ? 1 : 0));
        if (navigator.Current is not ObjectViewModel second)
        {
            return false;
        }

        await second.LoadTask;
        await second.ReferenceRows.WhenLoadedAsync();
        return first.Status is null && second.Status is null && (first.Fields.Count > 0 || first.FieldsUnavailable is not null || first.Text is not null);
    }

    /// <summary>
    /// Shows the Inspections tab (sprint 1.6): every inspection runs in the worker while the tab stays responsive; the
    /// rows of the selected one are fetched as the grid would. Returns whether every available inspection finished.
    /// </summary>
    private static async Task<bool> RunSmokeInspectionsAsync(SnapshotsViewModel snapshots, DispatcherWatchdog watchdog, ILogger logger)
    {
        watchdog.Reset();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        snapshots.SelectedTab = 2;
        var inspections = snapshots.Inspections;
        await inspections.RunTask;
        var rows = inspections.Rows;
        for (var i = 0; i < Math.Min(rows.Count, 50); i++)
        {
            rows.GetRow(i);
        }

        await rows.WhenLoadedAsync();
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var finished = inspections.Tiles.Count > 0 && inspections.Tiles.All(t => !t.IsAvailable || (t.Run is not null && t.Status is null));
        logger.LogInformation(
            "Smoke inspections: {Count} inspections in {Seconds:N1} s ({Findings}), selected {Selected}; maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation {OperationMs:N0} ms",
            inspections.Tiles.Count,
            stopwatch.Elapsed.TotalSeconds,
            string.Join("; ", inspections.Tiles.Select(t => $"{t.Info.Name}: {t.Status ?? t.Summary}")),
            inspections.Selected?.Title ?? "none",
            watchdog.MaxLag.TotalMilliseconds,
            watchdog.MaxOperation.TotalMilliseconds);
        snapshots.SelectedTab = 0;
        return finished;
    }

    /// <summary>
    /// Opens a second dump and shows the comparison (sprint 1.4): the pickers fill themselves with the two open snapshots,
    /// the grid's first rows are fetched as the grid would. Returns whether the comparison showed rows.
    /// </summary>
    private static async Task<bool> RunSmokeCompareAsync(SnapshotsViewModel snapshots, string dump, DispatcherWatchdog watchdog, ILogger logger)
    {
        // Show the (empty) tab first and let it render, then open: the tab's first layout and the rows are logged apart.
        snapshots.SelectedTab = 1;
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        logger.LogInformation("Smoke compare: tab shown");
        await snapshots.OpenPathAsync(dump);
        await snapshots.Compare.QueryTask;
        var rows = snapshots.Compare.Rows;
        for (var i = 0; i < Math.Min(rows.Count, 50); i++)
        {
            rows.GetRow(i);
        }

        await rows.WhenLoadedAsync();
        var shown = rows.Count > 0 && rows.GetRow(0).IsLoaded;
        logger.LogInformation(
            "Smoke compare: {Shown} ({Status}), {Rows:N0} rows, first {First}; {Totals}; first show (cold): maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation {OperationMs:N0} ms",
            shown,
            snapshots.Compare.Status ?? snapshots.Status ?? "ok",
            rows.Count,
            shown ? rows.GetRow(0).Name : "none",
            snapshots.Compare.Totals,
            watchdog.MaxLag.TotalMilliseconds,
            watchdog.MaxOperation.TotalMilliseconds);

        // The checked part, like the other smoke runs after their warm-up: a re-query and its rows in the warm grid
        // (one-time cold costs of the tab are logged above and tracked by the UI cold start backlog item).
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        watchdog.Reset();
        snapshots.Compare.SortByCommand.Execute(TypeDiffColumns.ObjectsDelta);
        await snapshots.Compare.QueryTask;
        rows = snapshots.Compare.Rows;
        for (var i = 0; i < Math.Min(rows.Count, 50); i++)
        {
            rows.GetRow(i);
        }

        await rows.WhenLoadedAsync();
        return shown && rows.Count > 0 && rows.GetRow(0).IsLoaded;
    }

    /// <summary>
    /// Last-chance handlers (ARCHITECTURE.md 11): log, tell the user in plain language, keep running. In smoke mode
    /// no dialog is shown so automated runs never block; the run fails instead.
    /// </summary>
    private void InstallLastChanceHandlers(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<App>>();
        var smoke = services.GetRequiredService<UiOptions>().IsSmokeRun;
        DispatcherUnhandledException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unhandled exception on the UI thread");
            e.Handled = true;
            if (smoke)
            {
                _exitCode = SmokeJobFailed;
                MainWindow?.Close();
                return;
            }

            MessageBox.Show(
                $"Something went wrong: {e.Exception.Message}\n\nHeapScope keeps running. Details are in the log at {Application.Logging.FileLoggerProvider.DefaultDirectory}.",
                "HeapScope",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception; HeapScope is terminating");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    /// <summary>
    /// Cancels running jobs and waits for them (bounded): a capture canceled here still lets the runtime finish writing
    /// and then deletes the dump, which must happen before the process exits (sprint 1.1 review H1).
    /// </summary>
    private static async Task CancelRunningJobsAsync(IServiceProvider services)
    {
        var running = services.GetRequiredService<Application.Jobs.JobTracker>().Jobs
            .Where(j => j.Status == Application.Jobs.JobStatus.Running)
            .ToList();
        if (running.Count == 0)
        {
            return;
        }

        if (Current?.MainWindow is { } window)
        {
            window.Title += " (finishing running jobs, a canceled capture removes its dump first)";
        }

        foreach (var job in running)
        {
            job.Cancel();
        }

        await Task.WhenAny(Task.WhenAll(running.Select(j => j.Completion)), Task.Delay(TimeSpan.FromMinutes(5)));
    }

    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_host is null)
        {
            return; // Host already stopped; let the window close.
        }

        e.Cancel = true;
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        var host = _host;
        try
        {
            // Monitoring first: once stopped, no rule starts a capture after the jobs are canceled (review of 1.7).
            host.Services.GetService<ShellViewModel>()?.Live?.StopCommand.Execute(null);
            await CancelRunningJobsAsync(host.Services);
            host.Services.GetService<ShellViewModel>()?.Dispose();
            host.Services.GetService<DispatcherWatchdog>()?.Dispose();
            using var timeout = new CancellationTokenSource(s_stopTimeout);
            await host.StopAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            // async void event handler: log and continue shutting down.
            host.Services.GetService<ILogger<App>>()?.LogError(ex, "Host did not stop cleanly");
        }
        finally
        {
            _host = null;
            if (host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                host.Dispose();
            }

            Shutdown(_exitCode);
        }
    }
}
