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
using HeapScope.Application.Snapshots;
using HeapScope.Capture;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using HeapScope.Core;

namespace HeapScope.Application.Sessions;

/// <summary>The outcome of a capture.</summary>
/// <param name="Path">Full path of the dump.</param>
/// <param name="SizeBytes">Size of the dump file.</param>
/// <param name="Process">The captured process.</param>
/// <param name="Session">The session it was recorded in, or null when recording failed (the dump is kept).</param>
/// <param name="Warning">A problem after the dump was written (for example the session could not be updated).</param>
/// <param name="Kind">What was captured: a full dump or a gcdump (sprint 1.8).</param>
public sealed record CaptureResult(string Path, long SizeBytes, ProcessEntry Process, Session? Session, string? Warning = null, SnapshotKindDto Kind = SnapshotKindDto.FullDump, Guid? SnapshotId = null)
{
    /// <summary>Shown once per capture (ARCHITECTURE.md 12).</summary>
    public string SecretsNote => Kind == SnapshotKindDto.GcDump
        ? $"The gcdump holds the process's object graph: type names, sizes and references, but no field values or string text. It was written to {Path}; HeapScope never uploads it."
        : $"The dump contains everything in the process's memory, including secrets such as connection strings and tokens. It was written to {Path}; HeapScope never uploads it.";
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
    private readonly GcDumpCapturer _gcDumpCapturer;
    private readonly IElevationProbe _elevation;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TrustedDacs? _trustedDacs;

    // One capture or forced collection per process at a time, across every page and rule (sprint 3.4, decision 3).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _busy = new();

    public CaptureService(
        IProcessDiscovery discovery,
        DumpCapturer capturer,
        SessionLibrary library,
        JobTracker jobs,
        IElevationProbe? elevation = null,
        TimeProvider? timeProvider = null,
        ILogger<CaptureService>? logger = null,
        GcDumpCapturer? gcDumpCapturer = null,
        TrustedDacs? trustedDacs = null)
    {
        _trustedDacs = trustedDacs;
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _capturer = capturer ?? throw new ArgumentNullException(nameof(capturer));
        _gcDumpCapturer = gcDumpCapturer ?? new GcDumpCapturer(elevation);
        Library = library ?? throw new ArgumentNullException(nameof(library));
        Jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _elevation = elevation ?? CapturePlatform.ElevationProbe();
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CaptureService>.Instance;

        // Off the constructing thread (the UI dispatcher at startup); the walk grows with the library.
        _ = Task.Run(() => SweepPartialDumps(null));
    }

    public SessionLibrary Library { get; }

    /// <summary>The job tracker captures run in (front ends show their progress).</summary>
    public JobTracker Jobs { get; }

    public bool IsHeapScopeElevated => _elevation.IsCurrentProcessElevated;

    public string? UnavailableReason => CapturePlatform.UnsupportedReason();

    public async Task<IReadOnlyList<ProcessEntry>> ListProcessesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var processes = await Task.Run(() => _discovery.List(cancellationToken), cancellationToken).ConfigureAwait(false);
            return [.. processes.Select(ToEntry)];
        }
        catch (PlatformNotSupportedException ex)
        {
            throw Failure(CaptureFailure.NotSupportedOnThisSystem, ex.Message, ex);
        }
    }

    /// <summary>
    /// Captures a full dump (or, with <paramref name="kind"/>, a gcdump) of <paramref name="processId"/> as a tracked job:
    /// into <paramref name="outputPath"/> when given, otherwise into the current session folder. The session is created
    /// only once the checks have passed, so a refused capture leaves no empty session. The target's DAC is copied next to
    /// a full dump, so the dump stays readable after the runtime on this machine is updated (sprint 1.1 review); a gcdump
    /// needs none.
    /// </summary>
    public TrackedJob<CaptureResult> StartCapture(
        int processId,
        string? outputPath = null,
        string? label = null,
        SnapshotSource source = SnapshotSource.Manual,
        SnapshotKindDto kind = SnapshotKindDto.FullDump)
    {
        var profile = ProfileOf(kind);
        var title = string.Create(CultureInfo.InvariantCulture, $"Capture {profile.TitlePrefix}pid {processId}");
        return Jobs.Start(title, async (progress, ct) =>
        {
            using var gate = EnterGate(processId, "a capture", label ?? source.ToString());
            var stopwatch = Stopwatch.StartNew();
            progress.Report(new JobProgress("Finding process", 0, null));
            DotNetProcess? found;
            try
            {
                found = await Task.Run(() => _discovery.Find(processId), ct).ConfigureAwait(false);
            }
            catch (PlatformNotSupportedException ex)
            {
                // Sprint 2.3.5 review: the refusal off Windows is a capture failure like any other.
                throw Failure(CaptureFailure.NotSupportedOnThisSystem, ex.Message, ex);
            }

            var process = found
                ?? throw Failure(CaptureFailure.ProcessNotFound, string.Create(CultureInfo.InvariantCulture, $"No running .NET process with id {processId} was found (or it has no diagnostics port)."), null);

            string? path = null;
            string? sessionFolder = null;
            var createdSession = false;

            // The session is written into until the capture is recorded: it cannot be deleted meanwhile (review H1 of 3.4).
            using var inUse = new Claim();
            try
            {
                if (outputPath is null)
                {
                    // Check against the library root before a session folder exists.
                    PrivateFiles.CreateDirectory(Library.Root);
                    profile.Preflight?.Invoke(process, Path.Combine(Library.Root, "probe" + profile.Extension));

                    createdSession = !Library.HasCurrent;

                    // The session is fixed now: opening another one meanwhile does not move this capture.
                    sessionFolder = Library.Current.Folder;
                    inUse.Held = Library.MarkInUse(sessionFolder, "a capture");
                    path = UniquePath(sessionFolder, process, profile.Extension);
                }
                else
                {
                    // The user's folder is not swept (sprint 3.7, peer review S4): its partial files may be someone else's.
                    path = Path.GetFullPath(Directory.Exists(outputPath) ? UniquePath(outputPath, process, profile.Extension) : outputPath);
                }

                _logger.LogInformation("Capturing a {Kind} of {Name} (pid {Pid}) to {Path}", profile.Noun, process.Name, process.ProcessId, path);
                await profile.Capture(process, path, new CaptureProgressAdapter(progress), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (Translate(ex) is { } failure)
            {
                _logger.LogWarning(ex, "Capture of pid {Pid} failed: {Reason}", processId, failure.Reason);
                if (createdSession && sessionFolder is not null)
                {
                    RemoveIfEmpty(sessionFolder);
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
                    RemoveIfEmpty(sessionFolder);
                }

                throw;
            }

            // Assigned in both branches of the try above, which returned or threw otherwise.
            var size = new FileInfo(path!).Length;
            _logger.LogInformation("Captured {Name} (pid {Pid}): {Bytes} bytes in {Seconds:N1} s", process.Name, process.ProcessId, size, stopwatch.Elapsed.TotalSeconds);
            if (profile.CopiesDac)
            {
                CopyDac(process, Path.GetDirectoryName(path!)!);
            }

            var notPrivate = MakePrivate(path!);
            try
            {
                var snapshotId = Guid.NewGuid();
                var session = Library.AddTo(sessionFolder ?? Library.Current.Folder, new SessionSnapshot(
                    snapshotId, path, process.Name, process.ProcessId, process.RuntimeVersion, _time.GetUtcNow(), size,
                    Kind: kind, Source: source, Label: label, RuntimeDirectory: process.RuntimeDirectory));
                _logger.LogInformation("Recorded {Path} as snapshot {SnapshotId} ({Source}, {Label}) in session {Folder}", path, snapshotId, source, label ?? "no label", session.Folder);
                return new CaptureResult(path, size, ToEntry(process), session, notPrivate, Kind: kind, SnapshotId: snapshotId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The snapshot is valid; only the record failed.
                _logger.LogWarning(ex, "The capture {Path} could not be recorded in its session", path);
                var warning = $"The snapshot was written, but the session could not be updated: {ex.Message}";
                return new CaptureResult(path, size, ToEntry(process), null, notPrivate is null ? warning : warning + " " + notPrivate, kind);
            }
        });
    }

    public bool IsBusy(int processId) => _busy.ContainsKey(processId);

    /// <summary>
    /// Makes a finished snapshot readable by this user only: the runtime wrote it with the folder's permissions, a folder the
    /// user picked may let others read it, and a dump holds every secret of the process (sprint 3.7, peer review P15). When
    /// that fails (an elevated capture of another user's process, whose account owns the file), the file keeps its
    /// permissions and the log and the result say so (3.7 review H6). Returns that warning, or null.
    /// </summary>
    private string? MakePrivate(string path)
    {
        try
        {
            PrivateFiles.Protect(path);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or InvalidOperationException)
        {
            _logger.LogWarning("The snapshot {Path} could not be made readable by this user only ({Error}: {Message}); it keeps its folder's permissions", path, ex.GetType().Name, ex.Message);
            return "HeapScope could not make the snapshot readable by you only, so it keeps its folder's permissions. A dump holds the process's secrets: if others can read this folder, move the file to a private one.";
        }
    }

    /// <summary>Removes the session a failed capture created, never hiding the capture's own failure (re-review).</summary>
    private void RemoveIfEmpty(string folder)
    {
        try
        {
            Library.DeleteIfEmpty(folder, "a capture");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(ex, "The session {Folder} the failed capture created was not removed", folder);
        }
    }

    public async Task<ForcedGcResult> ForceGcAsync(int processId, CancellationToken cancellationToken)
    {
        if (CapturePlatform.UnsupportedReason() is { } reason)
        {
            throw Failure(CaptureFailure.NotSupportedOnThisSystem, reason, null);
        }

        using var gate = EnterGate(processId, "a forced collection", "Force GC");
        try
        {
            // Decision 1: the runtime's GC heap collect keyword over EventPipe needs .NET 5 or later.
            var process = await Task.Run(() => _discovery.Find(processId), cancellationToken).ConfigureAwait(false)
                ?? throw Failure(CaptureFailure.ProcessNotFound, string.Create(CultureInfo.InvariantCulture, $"No running .NET process with id {processId} was found (or it has no diagnostics port)."), null);
            if (Version.TryParse(process.RuntimeVersion, out var runtime) && runtime.Major < 5)
            {
                _logger.LogInformation("Refused a forced collection of pid {Pid}: runtime {Runtime} is older than .NET 5", processId, process.RuntimeVersion);
                throw Failure(CaptureFailure.NotSupportedOnThisSystem, $"{process.Name} ({processId}) runs an older .NET than .NET 5, where HeapScope cannot force a collection.", null);
            }

            var forced = await GcTrigger.CollectAsync(processId, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            var result = new ForcedGcResult(processId, forced.Completed, forced.Elapsed, forced.GcNumber);
            _logger.LogInformation("Forced a full garbage collection in pid {Pid}: {Outcome} in {Seconds:0.00} s", processId, result.Outcome, forced.Elapsed.TotalSeconds);
            return result;
        }
        catch (Exception ex) when (ex is not CaptureFailedException && TranslateForcedGc(ex) is { } failure)
        {
            _logger.LogWarning(ex, "Forcing a garbage collection in pid {Pid} failed: {Reason}", processId, failure.Reason);
            throw failure;
        }
    }

    /// <summary>A forced collection's failure in its own words, not a dump's (review of 3.4).</summary>
    private static CaptureFailedException? TranslateForcedGc(Exception ex) => ex switch
    {
        ElevationRequiredException => Failure(CaptureFailure.NeedsAdministrator, ex.Message, ex),
        TargetUnavailableException => Failure(CaptureFailure.TargetExited, ex.Message, ex),
        PlatformNotSupportedException => Failure(CaptureFailure.NotSupportedOnThisSystem, ex.Message, ex),
        OperationCanceledException => null,
        _ => Failure(CaptureFailure.Unknown, $"The collection could not be forced: {ex.Message} Details are in the log.", ex),
    };

    /// <summary>
    /// Takes the process's gate, or fails with <see cref="CaptureFailure.Busy"/> naming what holds it (decision 3); the
    /// refusal is logged, so a skipped rule or a refused button can be backed up.
    /// </summary>
    private IDisposable EnterGate(int processId, string what, string why)
    {
        if (!_busy.TryAdd(processId, what))
        {
            var holder = _busy.TryGetValue(processId, out var running) ? running : "a capture";
            _logger.LogInformation("Refused {What} of pid {Pid} ({Why}): {Holder} of it is running", what, processId, why, holder);
            throw Failure(CaptureFailure.Busy, string.Create(CultureInfo.InvariantCulture, $"{char.ToUpperInvariant(holder[0])}{holder[1..]} of process {processId} is running. Wait for it to finish, then try again."), null);
        }

        return new Gate(() => _busy.TryRemove(processId, out _));
    }

    /// <summary>A claim taken part way through a job, released with it.</summary>
    private sealed class Claim : IDisposable
    {
        public IDisposable? Held { get; set; }

        public void Dispose() => Held?.Dispose();
    }

    private sealed class Gate(Action leave) : IDisposable
    {
        private int _left;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
            {
                leave();
            }
        }
    }

    /// <summary>
    /// What differs per kind of capture (sprint 1.9): a full dump is checked against the disk (its size is about the
    /// process's memory) and gets the target's DAC beside it; a gcdump is a fraction of the heap and needs no DAC.
    /// </summary>
    private CaptureProfile ProfileOf(SnapshotKindDto kind) => kind switch
    {
        SnapshotKindDto.GcDump => new(
            "gcdump",
            GcDumpCapturer.Extension,
            "gcdump of ",
            Preflight: null,
            CopiesDac: false,
            (process, path, progress, ct) => _gcDumpCapturer.CaptureAsync(process, path, progress, null, ct)),
        SnapshotKindDto.FullDump => new(
            "full dump",
            ".dmp",
            string.Empty,
            (process, path) => _capturer.Preflight(process, path),
            CopiesDac: true,
            _capturer.CaptureAsync),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), $"Unknown snapshot kind {kind}."),
    };

    private sealed record CaptureProfile(
        string Noun,
        string Extension,
        string TitlePrefix,
        Action<DotNetProcess, string>? Preflight,
        bool CopiesDac,
        Func<DotNetProcess, string, IProgress<CaptureProgress>, CancellationToken, Task> Capture);

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
            if (!File.Exists(source))
            {
                _logger.LogInformation("The target's runtime folder {Runtime} has no {Dac}; none is copied next to the dump", runtime, DacFileName);
                return;
            }

            if (!File.Exists(target))
            {
                PrivateFiles.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }

            TrustCopiedDac(source, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(ex, "The DAC {Source} was not copied next to the dump", source);
        }
    }

    /// <summary>
    /// Trusts the DAC a capture copied from the target's runtime, so opening the dump later does not ask (sprint 3.6,
    /// ADR-035), when the copy's content equals the runtime's file (a copy made by an earlier capture too). Not when
    /// HeapScope runs elevated (sprint 3.6 review): the target may belong to another, less privileged user who can write
    /// its runtime folder, and the worker would run that user's file elevated. The open asks then.
    /// </summary>
    private void TrustCopiedDac(string source, string target)
    {
        if (_trustedDacs is null)
        {
            return;
        }

        if (_elevation.IsCurrentProcessElevated)
        {
            _logger.LogInformation("The DAC {Target} is not trusted automatically: HeapScope runs elevated, so opening the dump asks first", target);
            return;
        }

        _trustedDacs.TrustFile(target, TrustedDacs.Sha256Of(source), $"copied by HeapScope's capture from {source}");
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
    private string UniquePath(string folder, DotNetProcess process, string extension)
    {
        var stamp = _time.GetUtcNow().ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = string.Create(CultureInfo.InvariantCulture, $"{process.Name}-{process.ProcessId}-{stamp}");
        var path = Path.Combine(folder, baseName + extension);
        for (var n = 2; File.Exists(path) || File.Exists(path + DumpCapturer.PartialSuffix); n++)
        {
            path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{baseName}-{n}{extension}"));
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
        PlatformNotSupportedException => Failure(CaptureFailure.NotSupportedOnThisSystem, ex.Message, ex),
        SessionUnreadableException => Failure(CaptureFailure.WriteFailed, ex.Message, ex),
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
