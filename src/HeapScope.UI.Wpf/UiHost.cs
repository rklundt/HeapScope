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
/// <param name="SmokeOpenPath">When set (<c>--smoke-open path</c>), the app opens this dump instead, waits for its overview
/// and exits with the same dispatcher checks (sprint 1.2).</param>
public sealed record UiOptions(int? SmokeSyntheticObjects, string? SmokeOpenPath = null)
{
    public bool IsSmokeRun => SmokeSyntheticObjects is not null || SmokeOpenPath is not null;
}

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
        builder.Services.Configure<WorkerHostOptions>(builder.Configuration.GetSection(WorkerHostOptions.SymbolsSectionName));
        builder.Services.Configure<HeapScope.Application.Sessions.SessionOptions>(builder.Configuration.GetSection(HeapScope.Application.Sessions.SessionOptions.SectionName));

        var smoke = builder.Configuration["smoke-synthetic"];
        builder.Services.AddSingleton(new UiOptions(
            smoke is null ? null : int.Parse(smoke, NumberStyles.None, CultureInfo.InvariantCulture),
            builder.Configuration["smoke-open"]));

        builder.Services.AddSingleton<IUiDispatcher>(new WpfUiDispatcher(dispatcher));
        builder.Services.AddSingleton(sp => new DispatcherWatchdog(dispatcher, sp.GetRequiredService<ILogger<DispatcherWatchdog>>()));
        builder.Services.AddSingleton<IUserInteraction>(_ => smoke is null && builder.Configuration["smoke-open"] is null ? new WpfUserInteraction() : new AutomaticUserInteraction());
        builder.Services.AddSingleton<SnapshotsViewModel>(sp => new SnapshotsViewModel(
            sp.GetRequiredService<HeapScope.Application.Snapshots.SnapshotSession>(),
            sp.GetRequiredService<WorkerOperations>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<IUserInteraction>(),
            sp.GetRequiredService<ILogger<TypeSummaryViewModel>>()));
        builder.Services.AddSingleton<HomeViewModel>(sp => new HomeViewModel(
            sp.GetRequiredService<WorkerOperations>(),
            sp.GetRequiredService<SnapshotsViewModel>(),
            sp.GetRequiredService<HeapScope.Application.Sessions.SessionLibrary>()));
        builder.Services.AddSingleton<ProcessesViewModel>(sp => new ProcessesViewModel(
            sp.GetRequiredService<HeapScope.Application.Sessions.ICaptureService>(),
            sp.GetRequiredService<SnapshotsViewModel>(),
            sp.GetRequiredService<IUserInteraction>()));
        builder.Services.AddSingleton<AboutViewModel>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        return builder.Build();
    }
}
