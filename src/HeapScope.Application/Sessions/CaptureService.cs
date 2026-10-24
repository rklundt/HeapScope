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

using System.Diagnostics;
using System.Globalization;
using HeapScope.Application.Jobs;
using HeapScope.Capture;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.Application.Sessions;

/// <summary>The outcome of a capture.</summary>
/// <param name="Path">Full path of the dump.</param>
/// <param name="SizeBytes">Size of the dump file.</param>
/// <param name="Process">The captured process.</param>
/// <param name="Session">The session it was recorded in, or null when recording failed (the dump is kept).</param>
/// <param name="Warning">A problem after the dump was written (for example the session could not be updated).</param>
public sealed record CaptureResult(string Path, long SizeBytes, ProcessEntry Process, Session? Session, string? Warning = null)
{
    /// <summary>Shown once per capture (ARCHITECTURE.md 12).</summary>
    public string SecretsNote => $"The dump contains everything in the process's memory, including secrets such as connection strings and tokens. It was written to {Path}; HeapScope never uploads it.";
}

/// <summary>
/// Process discovery and capture use cases shared by UI and CLI (ADR-008): list .NET processes, capture a full dump
/// into the current session as a tracked job, and record it in the session library. Results and failures use
/// Application types (<see cref="ProcessEntry"/>, <see cref="CaptureFailedException"/>).
/// </summary>
public sealed class CaptureService : ICaptureService
{
    private const string DacFileName = "mscordaccore.dll";

    private readonly IProcessDiscovery _discovery;
    private readonly DumpCapturer _capturer;
    private readonly IElevationProbe _elevation;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public CaptureService(
        IProcessDiscovery discovery,
        DumpCapturer capturer,
        SessionLibrary library,
        JobTracker jobs,
        IElevationProbe? elevation = null,
        TimeProvider? timeProvider = null,
        ILogger<CaptureService>? logger = null)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _capturer = capturer ?? throw new ArgumentNullException(nameof(capturer));
        Library = library ?? throw new ArgumentNullException(nameof(library));
        Jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _elevation = elevation ?? WindowsElevationProbe.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CaptureService>.Instance;

        // Off the constructing thread (the UI dispatcher at startup); the walk grows with the library.
        _ = Task.Run(() => SweepPartialDumps(null));
    }

    public SessionLibrary Library { get; }

    /// <summary>The job tracker captures run in (front ends show their progress).</summary>
    public JobTracker Jobs { get; }

    public bool IsHeapScopeElevated => _elevation.IsCurrentProcessElevated;

    public async Task<IReadOnlyList<ProcessEntry>> ListProcessesAsync(CancellationToken cancellationToken)
    {
        var processes = await Task.Run(() => _discovery.List(cancellationToken), cancellationToken).ConfigureAwait(false);
        return [.. processes.Select(ToEntry)];
    }

    /// <summary>
    /// Captures a full dump of <paramref name="processId"/> as a tracked job: into <paramref name="outputPath"/> when
    /// given, otherwise into the current session folder. The session is created only once the checks have passed, so a
    /// refused capture leaves no empty session. The target's DAC is copied next to the dump, so the dump stays readable
    /// after the runtime on this machine is updated (sprint 1.1 review).
    /// </summary>
    public TrackedJob<CaptureResult> StartCapture(int processId, string? outputPath = null, string? label = null, SnapshotSource source = SnapshotSource.Manual)
    {
        var title = string.Create(CultureInfo.InvariantCulture, $"Capture pid {processId}");
        return Jobs.Start(title, async (progress, ct) =>
        {
            var stopwatch = Stopwatch.StartNew();
            progress.Report(new JobProgress("Finding process", 0, null));
            var process = await Task.Run(() => _discovery.Find(processId), ct).ConfigureAwait(false)
                ?? throw Failure(CaptureFailure.ProcessNotFound, string.Create(CultureInfo.InvariantCulture, $"No running .NET process with id {processId} was found (or it has no diagnostics port)."), null);

            string? path = null;
            string? sessionFolder = null;
            var createdSession = false;
            try
            {
                if (outputPath is null)
                {
                    // Check against the library root before a session folder exists.
                    Directory.CreateDirectory(Library.Root);
                    _capturer.Preflight(process, Path.Combine(Library.Root, "probe.dmp"));
                    createdSession = !Library.HasCurrent;

                    // The session is fixed now: opening another one meanwhile does not move this capture.
                    sessionFolder = Library.Current.Folder;
                    path = UniquePath(sessionFolder, process);
                }
                else
                {
                    path = Path.GetFullPath(Directory.Exists(outputPath) ? UniquePath(outputPath, process) : outputPath);
                    SweepPartialDumps(Path.GetDirectoryName(path));
                }

                _logger.LogInformation("Capturing {Name} (pid {Pid}) to {Path}", process.Name, process.ProcessId, path);
                await _capturer.CaptureAsync(process, path, new CaptureProgressAdapter(progress), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (Translate(ex) is { } failure)
            {
                _logger.LogWarning(ex, "Capture of pid {Pid} failed: {Reason}", processId, failure.Reason);
                if (createdSession && sessionFolder is not null)
                {
                    Library.DeleteIfEmpty(sessionFolder);
                }

                throw failure;
            }
            catch (OperationCanceledException)
            {
                // The dump's own folder: a partial file still held there is removed later (retry or sweep).
                var partial = path is not null && File.Exists(path + DumpCapturer.PartialSuffix) ? [path + DumpCapturer.PartialSuffix] : new List<string>();
                if (partial.Count == 0)
                {
                    _logger.LogInformation("Capture of pid {Pid} canceled; the partial dump was removed", processId);
                }
                else
                {
                    _logger.LogWarning("Capture of pid {Pid} canceled; {Files} is still held and is removed in the background or at the next start", processId, string.Join(", ", partial));
                }

                if (createdSession && sessionFolder is not null)
                {
                    Library.DeleteIfEmpty(sessionFolder);
                }

                throw;
            }

            // Assigned in both branches of the try above, which returned or threw otherwise.
            var size = new FileInfo(path!).Length;
            _logger.LogInformation("Captured {Name} (pid {Pid}): {Bytes} bytes in {Seconds:N1} s", process.Name, process.ProcessId, size, stopwatch.Elapsed.TotalSeconds);
            CopyDac(process, Path.GetDirectoryName(path!)!);
            try
            {
                var session = Library.AddTo(sessionFolder ?? Library.Current.Folder, new SessionSnapshot(
                    Guid.NewGuid(), path, process.Name, process.ProcessId, process.RuntimeVersion, _time.GetUtcNow(), size,
                    Source: source, Label: label, RuntimeDirectory: process.RuntimeDirectory));
                return new CaptureResult(path, size, ToEntry(process), session);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The dump is valid; only the record failed.
                _logger.LogWarning(ex, "The capture {Path} could not be recorded in its session", path);
                return new CaptureResult(path, size, ToEntry(process), null, $"The dump was written, but the session could not be updated: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Copies the target's mscordaccore.dll next to the dump, so the 1.2 loader finds the matching DAC in the dump's
    /// folder even after the runtime on this machine is updated or for self-contained apps. A failed copy is logged; the
    /// dump stays readable while the runtime stays installed.
    /// </summary>
    private void CopyDac(DotNetProcess process, string folder)
    {
        if (process.RuntimeDirectory is not { } runtime)
        {
            return;
        }

        // One copy per runtime version (dac\<file version>), so dumps of two runtimes in one folder keep their own DAC.
        var source = Path.Combine(runtime, DacFileName);
        var target = Path.Combine(folder, "dac", process.RuntimeVersion ?? "unknown", DacFileName);
        try
        {
            if (File.Exists(source) && !File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(ex, "The DAC {Source} was not copied next to the dump", source);
        }
    }

    private void SweepPartialDumps(string? folder)
    {
        var removed = Library.SweepPartialDumps(folder);
        if (removed > 0)
        {
            _logger.LogInformation("Removed {Count} half-written dump(s) left by an interrupted capture", removed);
        }
    }

    /// <summary>A file name unique in <paramref name="folder"/> (counter suffix), so quick repeated captures never collide.</summary>
    private string UniquePath(string folder, DotNetProcess process)
    {
        var stamp = _time.GetUtcNow().ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = string.Create(CultureInfo.InvariantCulture, $"{process.Name}-{process.ProcessId}-{stamp}");
        var path = Path.Combine(folder, baseName + ".dmp");
        for (var n = 2; File.Exists(path) || File.Exists(path + DumpCapturer.PartialSuffix); n++)
        {
            path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{baseName}-{n}.dmp"));
        }

        return path;
    }

    private static CaptureFailedException? Translate(Exception ex) => ex switch
    {
        ElevationRequiredException => Failure(CaptureFailure.NeedsAdministrator, ex.Message, ex),
        InsufficientDiskSpaceException => Failure(CaptureFailure.InsufficientDiskSpace, ex.Message, ex),
        TargetUnavailableException => Failure(CaptureFailure.TargetExited, ex.Message, ex),
        DumpFileExistsException => Failure(CaptureFailure.FileExists, ex.Message, ex),
        DumpWriteFailedException => Failure(CaptureFailure.WriteFailed, ex.Message, ex),
        IOException or UnauthorizedAccessException => Failure(CaptureFailure.WriteFailed, $"The dump could not be written: {ex.Message}", ex),
        ArgumentException => Failure(CaptureFailure.WriteFailed, ex.Message, ex),
        OperationCanceledException => null,
        _ => Failure(CaptureFailure.Unknown, $"The capture failed: {ex.Message} Details are in the log.", ex),
    };

    private static CaptureFailedException Failure(CaptureFailure reason, string message, Exception? inner) => new(reason, message, inner);

    private ProcessEntry ToEntry(DotNetProcess process)
    {
        var elevation = process.Elevation switch
        {
            ElevationState.NotElevated => ProcessElevation.NotElevated,
            ElevationState.Elevated => ProcessElevation.Elevated,
            _ => ProcessElevation.Unknown,
        };
        return new ProcessEntry(
            process.ProcessId,
            process.Name,
            process.CommandLine,
            process.RuntimeVersion,
            elevation,
            elevation == ProcessElevation.Elevated && !IsHeapScopeElevated,
            process.WorkingSetBytes,
            process.PrivateBytes);
    }

    private sealed class CaptureProgressAdapter(IProgress<JobProgress> target) : IProgress<CaptureProgress>
    {
        public void Report(CaptureProgress value) => target.Report(new JobProgress(value.Phase, 0, null, value.Message));
    }
}
