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
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HeapScope.UI.Services;
using HeapScope.UI.Smoke;
using HeapScope.UI.ViewModels;
using HeapScope.UI.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI;

/// <summary>
/// Entry point and host lifecycle (ARCHITECTURE.md 8): the host starts asynchronously, and closing the main window stops
/// the host asynchronously (bounded) before the application shuts down. A stop requested by the host itself closes the
/// window through the same path. The app shuts down only when the host has stopped.
/// </summary>
public sealed partial class App : Avalonia.Application
{
    private static readonly TimeSpan s_stopTimeout = TimeSpan.FromSeconds(5);

    private readonly string[] _args;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
    private IHost? _host;
    private bool _stopping;
    private int _exitCode;

    public App()
        : this([])
    {
    }

    public App(string[] args) => _args = args ?? [];

    /// <summary>Process start to the main window's first frame; null until it is shown (the smoke gate checks it).</summary>
    public static TimeSpan? FirstWindowTime { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.UIThread.Post(() => _ = StartAsync());
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync()
    {
        // An interactive tool: never let Windows move the UI to efficiency cores when the window is in the background (ADR-019).
        if (OperatingSystem.IsWindows())
        {
            NativeMethods.DisableExecutionSpeedThrottling();
        }

        try
        {
            _host = UiHost.Build(_args, Dispatcher.UIThread);
            InstallLastChanceHandlers(_host.Services);
            var startLogger = _host.Services.GetRequiredService<ILogger<App>>();
            Services.AvaloniaLogSink.Target = startLogger;

            // What this run was started with (sprint 2.3.7 review H4); the log file's header names the build and system.
            startLogger.LogInformation(
                "{Product} {Version} desktop app starting: arguments {Arguments}; headless {Headless}; log folder {LogFolder}, level {LogLevel}",
                ProductName,
                Contracts.ProductInfo.InformationalVersion,
                _args.Length == 0 ? "(none)" : string.Join(' ', _args),
                Program.IsHeadless,
                _host.Services.GetRequiredService<Application.Logging.FileLoggerProvider>().Directory,
                _host.Services.GetRequiredService<Application.Logging.FileLoggerProvider>().MinimumLevel);
            RequestedThemeVariant = ThemeChooser.Choose(_host.Services.GetService<IConfiguration>()?["Ui:Theme"]);
            await _host.StartAsync();

            var lifetime = _host.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Register(() => Dispatcher.UIThread.Post(() => _window?.Close()));

            _host.Services.GetRequiredService<DispatcherWatchdog>();
            var window = _host.Services.GetRequiredService<MainWindow>();
            window.Closing += OnMainWindowClosing;
            window.Opened += (_, _) => Dispatcher.UIThread.Post(() => LogFirstWindow(_host.Services), DispatcherPriority.Background);
            _window = window;
            if (_desktop is not null)
            {
                _desktop.MainWindow = window;
            }

            window.Show();

            var options = _host.Services.GetRequiredService<UiOptions>();
            if (await new SmokeRunner(_host.Services).RunAsync(options) is int exitCode)
            {
                _exitCode = exitCode;
                window.Close();
            }
        }
        catch (Exception ex)
        {
            // Every exception of this fire-and-forget start is handled here. A smoke run is unattended: no dialog, it fails
            // with its exit code and the log says why (sprint 1.5 review).
            if (_host is null)
            {
                // Building the host failed (a bad option, a configuration error): no logger exists yet (review H4).
                Program.LogFatalStart(_args, ex);
            }
            else
            {
                _host.Services.GetService<ILoggerFactory>()?.CreateLogger<App>().LogError(ex, "Start failed");
            }

            if (_args.Any(a => a.StartsWith("--smoke", StringComparison.OrdinalIgnoreCase)))
            {
                _desktop?.Shutdown(SmokeRunner.SmokeJobFailed);
                return;
            }

            await MessageWindow.ShowAsync(null, ProductName, $"HeapScope could not start: {ex.Message}", MessageButtons.Ok);
            _desktop?.Shutdown(1);
        }
    }

    private const string ProductName = "HeapScope";

    /// <summary>Logs the cold start cost: process start to the first frame of the main window (backlog: UI cold start).</summary>
    private static void LogFirstWindow(IServiceProvider services)
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        FirstWindowTime = DateTime.Now - self.StartTime;
        services.GetRequiredService<ILogger<App>>().LogInformation("First window rendered {Ms:N0} ms after process start", FirstWindowTime.Value.TotalMilliseconds);
    }

    /// <summary>
    /// Last-chance handlers (ARCHITECTURE.md 11): log, tell the user in plain language, keep running. In smoke mode no
    /// dialog is shown so automated runs never block; the run fails instead.
    /// </summary>
    private void InstallLastChanceHandlers(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<App>>();
        var smoke = services.GetRequiredService<UiOptions>().IsSmokeRun;
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unhandled exception on the UI thread");
            e.Handled = true;
            if (smoke)
            {
                _exitCode = SmokeRunner.SmokeJobFailed;
                _window?.Close();
                return;
            }

            _ = MessageWindow.ShowAsync(
                _window,
                ProductName,
                $"Something went wrong: {e.Exception.Message}\n\nHeapScope keeps running. Details are in the log at {services.GetRequiredService<Application.Logging.FileLoggerProvider>().Directory}.",
                MessageButtons.Ok);
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
    private async Task CancelRunningJobsAsync(IServiceProvider services)
    {
        var running = services.GetRequiredService<Application.Jobs.JobTracker>().Jobs
            .Where(j => j.Status == Application.Jobs.JobStatus.Running)
            .ToList();
        if (running.Count == 0)
        {
            return;
        }

        if (_window is { } window)
        {
            window.Title += " (finishing running jobs, a canceled capture removes its dump first)";
        }

        foreach (var job in running)
        {
            job.Cancel();
        }

        await Task.WhenAny(Task.WhenAll(running.Select(j => j.Completion)), Task.Delay(TimeSpan.FromMinutes(5)));
    }

    private async void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
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
            var logger = host.Services.GetService<ILogger<App>>();
            try
            {
                if (host is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else
                {
                    host.Dispose();
                }
            }
            catch (Exception ex)
            {
                // A failing dispose must not keep the process alive without a window (sprint 2.3.6 review); the file
                // logger may already be gone, so the message can be lost.
                logger?.LogError(ex, "Host dispose failed");
            }

            _window?.Close();
            _desktop?.Shutdown(_exitCode);
        }
    }
}
