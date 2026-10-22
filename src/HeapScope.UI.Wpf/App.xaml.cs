using System.ComponentModel;
using System.Windows;
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _host = UiHost.Build(e.Args);
            await _host.StartAsync();

            var lifetime = _host.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Register(() => Dispatcher.BeginInvoke(() => MainWindow?.Close()));

            var window = _host.Services.GetRequiredService<MainWindow>();
            window.Closing += OnMainWindowClosing;
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            // async void event handler: every exception must be handled here.
            MessageBox.Show(
                $"HeapScope could not start: {ex.Message}",
                "HeapScope",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
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

            Shutdown();
        }
    }
}
