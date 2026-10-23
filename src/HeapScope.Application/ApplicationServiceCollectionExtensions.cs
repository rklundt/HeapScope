using HeapScope.Application.Jobs;
using HeapScope.Application.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HeapScope.Application;

/// <summary>Registers the Application layer for UI and CLI hosts.</summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Adds the worker host (also as a hosted service, so stopping the host stops the worker in order), the job
    /// tracker and the shared operations. Configure <see cref="WorkerHostOptions"/> separately.
    /// </summary>
    public static IServiceCollection AddHeapScopeApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<WorkerHostOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<WorkerHost>();
        services.AddSingleton<IWorkerClient>(sp => sp.GetRequiredService<WorkerHost>());
        services.AddHostedService(sp => sp.GetRequiredService<WorkerHost>());
        services.AddSingleton<JobTracker>();
        services.AddSingleton<WorkerOperations>();
        services.AddSingleton<Snapshots.SnapshotSession>();
        return services;
    }
}
