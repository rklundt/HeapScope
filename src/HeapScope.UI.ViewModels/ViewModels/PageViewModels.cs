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

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application;
using HeapScope.Application.Jobs;
using HeapScope.Application.Sessions;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;

/// <summary>
/// Home page: open a dump (sprint 1.2), recent sessions with their snapshots (sprint 1.1, dotMemory's workspace list)
/// and the synthetic diagnostic job (sprint 0.3).
/// </summary>
public sealed partial class HomeViewModel(WorkerOperations operations, SnapshotsViewModel? snapshots = null, SessionLibrary? library = null, IUserInteraction? interaction = null, Microsoft.Extensions.Logging.ILogger<HomeViewModel>? logger = null) : ObservableObject
{
    private readonly WorkerOperations _operations = operations ?? throw new ArgumentNullException(nameof(operations));

    /// <summary>Sessions not opened for this many days are offered by "Clean up old sessions" (sprint 3.4).</summary>
    [ObservableProperty]
    private int _cleanupDays = 30;

    /// <summary>
    /// Deletes a session with its snapshots and their caches (sprint 3.4, decision 6), after the user confirms the files;
    /// refused for the current session, one with an open snapshot, and one something writes into. Every step is logged.
    /// </summary>
    [RelayCommand]
    private async Task DeleteSessionAsync(SessionSummary? summary)
    {
        if (summary is null || library is null || interaction is null)
        {
            return;
        }

        if (WhyKept(summary) is { } why)
        {
            logger?.LogInformation("Deleting session {Folder} refused: {Reason}", summary.Folder, why);
            SessionStatus = why;
            return;
        }

        Deletion plan;
        try
        {
            plan = await Task.Run(() => library.PlanSessionDeletion(summary.Folder));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            logger?.LogInformation("Deleting session {Folder} refused: {Reason}", summary.Folder, ex.Message);
            SessionStatus = ex.Message;
            return;
        }

        var question = string.Create(CultureInfo.CurrentCulture, $"Delete the session {summary.Name} with its {summary.SnapshotCount} snapshots and their caches, {plan.Files.Count} files ({Format.Bytes(plan.Bytes)})? This cannot be undone.");
        var yes = await interaction.ConfirmAsync("Delete session", question + Environment.NewLine + DeletionText.Files(plan));
        logger?.LogInformation("Asked to delete session {Folder} ({Files} files, {Bytes} bytes, {Kept} kept outside it); the user answered {Answer}", summary.Folder, plan.Files.Count, plan.Bytes, plan.Kept?.Count ?? 0, yes ? "yes" : "no");
        if (!yes)
        {
            return;
        }

        var outcome = await DeleteAsync([summary]);
        await RefreshSessionsAsync();
        SessionStatus = outcome;
    }

    /// <summary>
    /// Proposes the sessions not opened for <see cref="CleanupDays"/> days, each with its size, and deletes them after
    /// confirmation (sprint 3.4); the cutoff, what was proposed, what was kept and why, and the answer are logged.
    /// </summary>
    [RelayCommand]
    private async Task CleanUpAsync()
    {
        if (library is null || interaction is null)
        {
            return;
        }

        var days = Math.Max(1, CleanupDays);
        var old = await Task.Run(() => library.ListOlderThan(TimeSpan.FromDays(days)));
        var deletable = new List<SessionSummary>();
        foreach (var session in old)
        {
            if (WhyKept(session) is { } why)
            {
                logger?.LogInformation("Clean up keeps session {Folder}: {Reason}", session.Folder, why);
            }
            else
            {
                deletable.Add(session);
            }
        }

        logger?.LogInformation("Clean up of sessions not opened for {Days} days: {Old} found, {Proposed} proposed: {Folders}", days, old.Count, deletable.Count, string.Join("; ", deletable.Select(s => s.Folder)));
        if (deletable.Count == 0)
        {
            SessionStatus = string.Create(CultureInfo.CurrentCulture, $"No session was left unopened for {days} days{(old.Count > deletable.Count ? " (sessions in use are kept)" : string.Empty)}.");
            return;
        }

        var names = string.Join(Environment.NewLine, deletable.Take(10).Select(s => string.Create(CultureInfo.CurrentCulture, $"  {s.Name}: {s.SnapshotCount} snapshots, {Format.Bytes(s.SizeBytes)}, last opened {s.LastOpenedUtc.LocalDateTime:d}")))
            + (deletable.Count > 10 ? string.Create(CultureInfo.CurrentCulture, $"{Environment.NewLine}  and {deletable.Count - 10} more") : string.Empty);
        var yes = await interaction.ConfirmAsync("Clean up old sessions", string.Create(CultureInfo.CurrentCulture, $"Delete {deletable.Count} sessions not opened for {days} days ({Format.Bytes(deletable.Sum(s => s.SizeBytes))})? This cannot be undone. Dumps a session recorded outside its folder are kept.{Environment.NewLine}{names}"));
        logger?.LogInformation(
            "Asked to clean up {Count} sessions ({Bytes} bytes): {Sessions}; the user answered {Answer}",
            deletable.Count,
            deletable.Sum(s => s.SizeBytes),
            string.Join("; ", deletable.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Folder} {s.SizeBytes} bytes, last opened {s.LastOpenedUtc:O}"))),
            yes ? "yes" : "no");
        if (!yes)
        {
            return;
        }

        var outcome = await DeleteAsync(deletable);
        await RefreshSessionsAsync();
        SessionStatus = outcome;
    }

    /// <summary>Why a session is not deleted now, or null (the library refuses the same, under its own checks).</summary>
    private string? WhyKept(SessionSummary summary)
    {
        var folder = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(summary.Folder));
        if (string.Equals(library?.CurrentFolder, folder, StringComparison.OrdinalIgnoreCase))
        {
            return $"{summary.Name} is the current session (new captures go into it), so it is kept. Open another session first.";
        }

        if (library?.InUseBy(folder) is { } user)
        {
            return $"{summary.Name} is in use: {user} writes into it. Stop it first.";
        }

        var under = folder + System.IO.Path.DirectorySeparatorChar;
        return snapshots?.Snapshots.Any(s => System.IO.Path.GetFullPath(s.Handle.Path).StartsWith(under, StringComparison.OrdinalIgnoreCase)) == true
            ? $"{summary.Name} has a snapshot open. Close it on the Snapshots page first."
            : null;
    }

    private async Task<string> DeleteAsync(IReadOnlyList<SessionSummary> sessions)
    {
        long freed = 0;
        var failed = new List<string>();
        foreach (var session in sessions)
        {
            try
            {
                var deletion = await Task.Run(() => library!.DeleteSession(session.Folder));
                freed += deletion.Bytes;
                DeletionText.Log(logger, "session " + session.Folder, deletion);
                if (!deletion.Complete)
                {
                    failed.Add(System.IO.File.Exists(System.IO.Path.Combine(session.Folder, SessionLibrary.DocumentName))
                        ? $"{session.Name} ({deletion.Failed!.Count} files are in use: {string.Join("; ", deletion.Failed)}; it is still listed and can be deleted again)"
                        : $"{session.Name} (its files are deleted but its folder could not be removed: {string.Join("; ", deletion.Failed!)}; delete it yourself)");
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                logger?.LogWarning(ex, "Deleting session {Folder} was refused or failed", session.Folder);
                failed.Add($"{session.Name} ({ex.Message})");
            }
        }

        return string.Create(CultureInfo.CurrentCulture, $"Deleted {sessions.Count - failed.Count} sessions, {Format.Bytes(freed)} freed.") + (failed.Count == 0 ? string.Empty : " Not deleted: " + string.Join("; ", failed));
    }

    /// <summary>Sessions, most recently opened first (name, created, last opened, snapshots, size).</summary>
    public ObservableCollection<SessionSummary> RecentSessions { get; } = [];

    [ObservableProperty]
    private string? _sessionStatus;

    /// <summary>Reloads the recent sessions list (read off the dispatcher).</summary>
    [RelayCommand]
    private async Task RefreshSessionsAsync()
    {
        if (library is null)
        {
            return;
        }

        try
        {
            var sessions = await Task.Run(() => library.ListRecent());
            RecentSessions.Clear();
            foreach (var session in sessions)
            {
                RecentSessions.Add(session);
            }

            SessionStatus = sessions.Count == 0 ? "No sessions yet. Capture a process or open a dump to start one." : null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            SessionStatus = "Sessions could not be read: " + ex.Message;
        }
    }

    /// <summary>Opens the most recent snapshot of a session through the Snapshots page.</summary>
    [RelayCommand]
    private async Task OpenSessionAsync(SessionSummary? summary)
    {
        if (summary is null || library is null || snapshots is null)
        {
            return;
        }

        Session? session;
        try
        {
            // Opening a session makes it current: the next capture goes into it.
            session = await Task.Run(() => library.Open(summary.Folder));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            SessionStatus = $"The session {summary.Name} could not be opened: {ex.Message}";
            return;
        }

        if (session is null)
        {
            SessionStatus = $"The session {summary.Name} is damaged or was removed.";
            return;
        }

        if (session.Document.Snapshots.LastOrDefault() is not { } latest)
        {
            SessionStatus = $"The session {summary.Name} has no snapshots yet; new captures go into it.";
            return;
        }

        await snapshots.OpenPathAsync(session.PathOf(latest));
    }

    /// <summary>Opens a dump through the Snapshots page, which the shell then shows.</summary>
    public IAsyncRelayCommand? OpenDumpCommand => snapshots?.OpenDumpCommand;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunSyntheticCommand))]
    private int _objectCount = 5_000_000;

    [ObservableProperty]
    private string? _lastResult;

    /// <summary>The most recent synthetic job (smoke tests await it).</summary>
    public TrackedJob<SyntheticJobResult>? LastJob { get; private set; }

    private bool CanRunSynthetic() => ObjectCount is >= 0 and <= 100_000_000;

    [RelayCommand(CanExecute = nameof(CanRunSynthetic), AllowConcurrentExecutions = true)]
    private async Task RunSyntheticAsync()
    {
        var job = _operations.StartSyntheticJob(new SyntheticJobRequest(ObjectCount));
        LastJob = job;
        try
        {
            var result = await job.Result;
            LastResult = string.Create(
                CultureInfo.CurrentCulture,
                $"{result.ObjectCount:N0} objects, {result.ReferenceCount:N0} references, {result.TotalSize / (1024.0 * 1024.0):N1} MB, built in {result.ElapsedSeconds:N2} s");
            await _operations.ReleaseResultSetAsync(result.TypeSummary.Id, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            LastResult = "Canceled.";
        }
        catch (Exception ex) when (ex is WorkerException)
        {
            LastResult = ex.Message;
        }
    }
}

/// <summary>Jobs page: the full job list with a way to clear finished jobs.</summary>
public sealed partial class JobsPageViewModel(ObservableCollection<JobViewModel> jobs, JobTracker tracker) : ObservableObject
{
    public ObservableCollection<JobViewModel> Jobs { get; } = jobs ?? throw new ArgumentNullException(nameof(jobs));

    [RelayCommand]
    private void ClearFinished()
    {
        tracker.ClearFinished();
        for (var i = Jobs.Count - 1; i >= 0; i--)
        {
            if (Jobs[i].Status != JobStatus.Running)
            {
                Jobs.RemoveAt(i);
            }
        }
    }
}

/// <summary>
/// About page: product and worker versions, what HeapScope runs on and where its logs are, with a copy action for support
/// (sprint 2.3.7 review M16).
/// </summary>
public sealed partial class AboutViewModel(WorkerOperations operations, IUserInteraction? interaction = null, string? logFolder = null) : ObservableObject
{
    [ObservableProperty]
    private string _workerDetails = "Not queried yet.";

    public string ProductVersion { get; } = $"{ProductInfo.Name} {ProductInfo.Version}";

    /// <summary>The build with its source revision, the system, architecture and runtime.</summary>
    public string Environment { get; } = RuntimeEnvironment.Summary;

    /// <summary>Where this run writes its log; null when it is not known.</summary>
    public string? LogFolder { get; } = logFolder;

    /// <summary>The keyboard shortcuts (sprint 2.7), set by the shell, which knows the pages.</summary>
    public IReadOnlyList<Shortcut> Shortcuts { get; set; } = [];

    /// <summary>The details above as one text, for a support request.</summary>
    [RelayCommand]
    private Task CopyDetailsAsync() => interaction?.CopyTextAsync(
        $"{Environment}{System.Environment.NewLine}Log folder: {LogFolder ?? "unknown"}{System.Environment.NewLine}{WorkerDetails}") ?? Task.CompletedTask;

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await operations.GetInfoAsync(cancellationToken);
            WorkerDetails = string.Create(
                CultureInfo.CurrentCulture,
                $"Worker {info.ProductVersion}, protocol {info.ProtocolVersion}, pid {info.ProcessId}, server GC {info.ServerGc}, working set {info.WorkingSetBytes / (1024.0 * 1024.0):N0} MB");
        }
        catch (Exception ex) when (ex is WorkerException or OperationCanceledException)
        {
            WorkerDetails = ex.Message;
        }
    }
}
