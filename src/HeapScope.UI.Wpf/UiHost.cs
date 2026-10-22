using System.Globalization;
using HeapScope.Application;
using HeapScope.Application.Logging;
using HeapScope.Application.Workers;
using HeapScope.UI.Wpf.Services;
using HeapScope.UI.Wpf.ViewModels;
using HeapScope.UI.Wpf.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.Wpf;

/// <summary>Options read from the UI's command line.</summary>
/// <param name="SmokeSyntheticObjects">When set (<c>--smoke-synthetic N</c>), the app runs one synthetic job of N
/// objects, records the maximum dispatcher lag and exits (sprint 0.3 decision 7).</param>
public sealed record UiOptions(int? SmokeSyntheticObjects);

/// <summary>Builds the generic host (DI, configuration, logging) for the WPF app.</summary>
internal static class UiHost
{
    public static IHost Build(string[] args, System.Windows.Threading.Dispatcher dispatcher)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddProvider(new FileLoggerProvider(builder.Configuration["Logging:File:Directory"]));

        builder.Services.AddHeapScopeApplication();
        builder.Services.Configure<WorkerHostOptions>(builder.Configuration.GetSection(WorkerHostOptions.SectionName));

        var smoke = builder.Configuration["smoke-synthetic"];
        builder.Services.AddSingleton(new UiOptions(
            smoke is null ? null : int.Parse(smoke, NumberStyles.None, CultureInfo.InvariantCulture)));

        builder.Services.AddSingleton<IUiDispatcher>(new WpfUiDispatcher(dispatcher));
        builder.Services.AddSingleton(sp => new DispatcherWatchdog(dispatcher, sp.GetRequiredService<ILogger<DispatcherWatchdog>>()));
        builder.Services.AddSingleton<HomeViewModel>();
        builder.Services.AddSingleton<AboutViewModel>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        return builder.Build();
    }
}
