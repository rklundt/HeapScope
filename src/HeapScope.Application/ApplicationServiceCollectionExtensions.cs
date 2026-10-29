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
        services.AddOptions<Sessions.SessionOptions>();
        services.TryAddSingleton(_ => Capture.CapturePlatform.ElevationProbe());
        services.TryAddSingleton<Capture.IProcessDiscovery>(sp => new Capture.ProcessDiscovery(sp.GetRequiredService<Capture.IElevationProbe>()));
        services.TryAddSingleton(sp => new Capture.DumpCapturer(sp.GetRequiredService<Capture.IElevationProbe>()));
        services.TryAddSingleton(sp => new Capture.GcDumpCapturer(
            sp.GetRequiredService<Capture.IElevationProbe>(),
            new Capture.EventPipeGcDumpWriter(sp.GetService<Microsoft.Extensions.Logging.ILogger<Capture.EventPipeGcDumpWriter>>())));
        services.TryAddSingleton(sp => new Sessions.SessionLibrary(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Sessions.SessionOptions>>().Value.Root,
            sp.GetRequiredService<TimeProvider>()));
        // Bookmarks live beside the session library (sprint 2.7), so a test's or smoke run's library keeps them too.
        services.TryAddSingleton(sp => new Bookmarks.BookmarkStore(Path.GetDirectoryName(sp.GetRequiredService<Sessions.SessionLibrary>().Root)));
        services.AddSingleton(sp => new Sessions.CaptureService(
            sp.GetRequiredService<Capture.IProcessDiscovery>(),
            sp.GetRequiredService<Capture.DumpCapturer>(),
            sp.GetRequiredService<Sessions.SessionLibrary>(),
            sp.GetRequiredService<JobTracker>(),
            sp.GetRequiredService<Capture.IElevationProbe>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<Sessions.CaptureService>>(),
            sp.GetRequiredService<Capture.GcDumpCapturer>()));
        services.AddSingleton<Sessions.ICaptureService>(sp => sp.GetRequiredService<Sessions.CaptureService>());
        services.AddSingleton<Sessions.TraceService>();
        services.AddSingleton(sp => new Allocations.AllocationSession(
            sp.GetRequiredService<IWorkerClient>(),
            sp.GetRequiredService<JobTracker>(),
            sp.GetRequiredService<Sessions.TraceService>(),
            sp.GetRequiredService<Sessions.SessionLibrary>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<Allocations.AllocationSession>>()));
        services.TryAddSingleton<Monitoring.ICounterSource, Monitoring.EventPipeCounterSource>();
        services.AddSingleton(sp => new Monitoring.MonitorService(
            sp.GetRequiredService<Sessions.ICaptureService>(),
            sp.GetRequiredService<Monitoring.ICounterSource>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<Monitoring.MonitorService>>()));
        return services;
    }
}
