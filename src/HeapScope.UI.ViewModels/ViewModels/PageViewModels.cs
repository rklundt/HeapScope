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
    /// Deletes a session with its snapshots and their caches (sprint 3.4, decision 6), after confirmation; refused for the
    /// current session and one with an open snapshot.
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
            SessionStatus = why;
            return;
        }

        if (!await interaction.ConfirmAsync("Delete session", string.Create(System.Globalization.CultureInfo.CurrentCulture, $"Delete the session {summary.Name} with its {summary.SnapshotCount} snapshots and their caches ({Format.Bytes(summary.SizeBytes)})? This cannot be undone.")))
        {
            return;
        }

        var outcome = await DeleteAsync([summary]);
        await RefreshSessionsAsync();
        SessionStatus = outcome;
    }

    /// <summary>Proposes the sessions not opened for <see cref="CleanupDays"/> days and deletes them after confirmation (sprint 3.4).</summary>
    [RelayCommand]
    private async Task CleanUpAsync()
    {
        if (library is null || interaction is null)
        {
            return;
        }

        var days = Math.Max(1, CleanupDays);
        var old = await Task.Run(() => library.ListOlderThan(TimeSpan.FromDays(days)));
        var deletable = old.Where(s => WhyKept(s) is null).ToList();
        if (deletable.Count == 0)
        {
            SessionStatus = string.Create(System.Globalization.CultureInfo.CurrentCulture, $"No session was left unopened for {days} days{(old.Count > deletable.Count ? " (sessions with an open snapshot are kept)" : string.Empty)}.");
            return;
        }

        var names = string.Join(", ", deletable.Take(10).Select(s => s.Name)) + (deletable.Count > 10 ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $" and {deletable.Count - 10} more") : string.Empty);
        if (!await interaction.ConfirmAsync("Clean up old sessions", string.Create(System.Globalization.CultureInfo.CurrentCulture, $"Delete {deletable.Count} sessions not opened for {days} days ({Format.Bytes(deletable.Sum(s => s.SizeBytes))}): {names}? This cannot be undone.")))
        {
            return;
        }

        var outcome = await DeleteAsync(deletable);
        await RefreshSessionsAsync();
        SessionStatus = outcome;
    }

    /// <summary>Why a session is not deleted now, or null.</summary>
    private string? WhyKept(SessionSummary summary)
    {
        var folder = System.IO.Path.GetFullPath(summary.Folder).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (library is { HasCurrent: true } && string.Equals(System.IO.Path.GetFullPath(library.Current.Folder).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, folder, StringComparison.OrdinalIgnoreCase))
        {
            return $"{summary.Name} is the current session (new captures go into it), so it is kept. Open another session first.";
        }

        return snapshots?.Snapshots.Any(s => System.IO.Path.GetFullPath(s.Handle.Path).StartsWith(folder, StringComparison.OrdinalIgnoreCase)) == true
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
                logger?.LogInformation("Deleted session {Folder}: {Files} files, {Bytes} bytes", session.Folder, deletion.Files.Count, deletion.Bytes);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                logger?.LogWarning(ex, "Deleting session {Folder} failed", session.Folder);
                failed.Add($"{session.Name} ({ex.Message})");
            }
        }

        return string.Create(System.Globalization.CultureInfo.CurrentCulture, $"Deleted {sessions.Count - failed.Count} sessions, {Format.Bytes(freed)} freed.") + (failed.Count == 0 ? string.Empty : " Not deleted: " + string.Join("; ", failed));
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
