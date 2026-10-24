using System.IO;
using HeapScope.Application.Jobs;
using HeapScope.Application.Sessions;

namespace HeapScope.UI.Wpf.Tests;

/// <summary>Capture use cases for view model tests: a scripted process list and captures that write a small file.</summary>
internal sealed class FakeCaptureService(string sessionsRoot, JobTracker jobs) : ICaptureService
{
    public SessionLibrary Library { get; } = new(sessionsRoot);

    public bool IsHeapScopeElevated { get; set; }

    public List<ProcessEntry> Processes { get; } = [];

    /// <summary>When set, captures fail with it.</summary>
    public CaptureFailedException? Failure { get; set; }

    public Task<IReadOnlyList<ProcessEntry>> ListProcessesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProcessEntry>>([.. Processes.Select(p => p with { NeedsAdministrator = p.Elevation == ProcessElevation.Elevated && !IsHeapScopeElevated })]);

    public TrackedJob<CaptureResult> StartCapture(int processId, string? outputPath = null, string? label = null, SnapshotSource source = SnapshotSource.Manual) =>
        jobs.Start($"Capture pid {processId}", async (progress, ct) =>
        {
            if (Failure is { } failure)
            {
                throw failure;
            }

            var process = Processes.Single(p => p.ProcessId == processId);
            if (process.Elevation == ProcessElevation.Elevated && !IsHeapScopeElevated)
            {
                throw new CaptureFailedException(CaptureFailure.NeedsAdministrator, "Run HeapScope as administrator and capture again.", null);
            }

            var path = outputPath ?? Path.Combine(Library.Current.Folder, $"{process.Name}-{processId}.dmp");
            await File.WriteAllBytesAsync(path, new byte[128], ct);
            var session = Library.AddToCurrent(new SessionSnapshot(Guid.NewGuid(), path, process.Name, processId, process.RuntimeVersion, DateTimeOffset.UtcNow, 128, Label: label));
            return new CaptureResult(path, 128, process, session);
        });
}
