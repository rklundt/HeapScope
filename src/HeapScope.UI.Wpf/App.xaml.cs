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

using System.ComponentModel;
using System.Windows;
using HeapScope.Contracts;
using HeapScope.UI.Wpf.Services;
using HeapScope.UI.Wpf.Smoke;
using HeapScope.UI.ViewModels;
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
            if (await new SmokeRunner(_host.Services, Dispatcher).RunAsync(options) is int exitCode)
            {
                _exitCode = exitCode;
                MainWindow?.Close();
            }
        }
        catch (Exception ex)
        {
            // async void event handler: every exception must be handled here. A smoke run is unattended: no dialog, it
            // fails with its exit code and the log says why (sprint 1.5 review).
            if (e.Args.Any(a => a.StartsWith("--smoke", StringComparison.OrdinalIgnoreCase)))
            {
                _host?.Services.GetService<ILoggerFactory>()?.CreateLogger<App>().LogError(ex, "Smoke run failed");
                Shutdown(SmokeRunner.SmokeJobFailed);
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
                _exitCode = SmokeRunner.SmokeJobFailed;
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
