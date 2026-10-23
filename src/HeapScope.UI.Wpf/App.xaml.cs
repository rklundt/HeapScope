using System.ComponentModel;
using System.Windows;
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
        base.OnStartup(e);
        try
        {
            _host = UiHost.Build(e.Args, Dispatcher);
            InstallLastChanceHandlers(_host.Services);
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
        }
        catch (Exception ex)
        {
            // async void event handler: every exception must be handled here.
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
    private async Task RunSmokeOpenAsync(string dump)
    {
        var services = _host!.Services;
        var logger = services.GetRequiredService<ILogger<App>>();
        var watchdog = services.GetRequiredService<DispatcherWatchdog>();
        var home = services.GetRequiredService<HomeViewModel>();
        var snapshots = services.GetRequiredService<SnapshotsViewModel>();

        // Warm-up as in RunSmokeAsync, so one-time cold costs are not counted.
        home.ObjectCount = 10_000;
        await home.RunSyntheticCommand.ExecuteAsync(null);
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(500);
        watchdog.Reset();

        await snapshots.OpenPathAsync(dump);
        await snapshots.OverviewLoad;
        var completed = snapshots.Selected is not null && snapshots.TopTypes.Count > 0;
        await Task.Delay(500);
        var lag = watchdog.MaxLag;
        var operation = watchdog.MaxOperation;
        logger.LogInformation(
            "Smoke open: snapshot shown {Completed} ({Status}), maximum dispatcher lag {LagMs:N0} ms, longest dispatcher operation {OperationMs:N0} ms",
            completed,
            snapshots.Status ?? "ok",
            lag.TotalMilliseconds,
            operation.TotalMilliseconds);
        _exitCode = !completed ? SmokeJobFailed
            : operation > DispatcherWatchdog.Budget || lag > SmokeLagLimit ? SmokeLagExceeded
            : 0;
        MainWindow?.Close();
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
