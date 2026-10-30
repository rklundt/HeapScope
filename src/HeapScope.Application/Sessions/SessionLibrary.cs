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

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HeapScope.Contracts;
using HeapScope.Core;

namespace HeapScope.Application.Sessions;

/// <summary>Configuration section "Sessions".</summary>
public sealed class SessionOptions
{
    public const string SectionName = "Sessions";

    /// <summary>Folder holding one subfolder per session; defaults to <c>%LOCALAPPDATA%\HeapScope\sessions</c>.</summary>
    public string? Root { get; set; }
}

/// <summary>How a snapshot came into the session.</summary>
public enum SnapshotSource
{
    /// <summary>Captured on request (UI or CLI).</summary>
    Manual = 0,

    /// <summary>Captured by a rule (timed or threshold snapshots, sprint 1.7).</summary>
    Timed = 1,

    /// <summary>An existing file added to the session.</summary>
    Imported = 2,
}

/// <summary>One snapshot recorded in a session (paths are relative to the session folder when inside it).</summary>
/// <param name="Id">Stable id, so diffs and timelines (1.4, 1.7) can refer to a snapshot.</param>
/// <param name="File">The file, relative to the session folder when inside it.</param>
/// <param name="Label">Optional label such as "baseline".</param>
/// <param name="RuntimeDirectory">Folder of the target's runtime at capture time (where its DAC came from).</param>
public sealed record SessionSnapshot(
    Guid Id,
    string File,
    string? ProcessName,
    int? ProcessId,
    string? RuntimeVersion,
    DateTimeOffset CapturedUtc,
    long SizeBytes,
    SnapshotKindDto Kind = SnapshotKindDto.FullDump,
    SnapshotSource Source = SnapshotSource.Manual,
    string? Label = null,
    string? RuntimeDirectory = null)
{
    /// <summary>Members written by a newer HeapScope, kept when this build saves the document.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// The <c>session.json</c> document (format version 1). Forward compatibility: a build keeps members it does not know
/// (<see cref="Extra"/>), so an older build never drops what a newer one wrote; a new format version is needed only for
/// changes an older build would misread.
/// </summary>
public sealed record SessionDocument(
    int FormatVersion,
    string Name,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastOpenedUtc,
    IReadOnlyList<SessionSnapshot> Snapshots,
    IReadOnlyList<SessionRun>? Runs = null)
{
    public const int CurrentFormatVersion = 1;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// A monitoring run recorded in its session (sprint 3.4, decision 5): what was watched and how, when, and the files and
/// snapshots it produced (paths relative to the session folder). <see cref="EndedUtc"/> is null while it runs (or when the
/// app ended without recording its end).
/// </summary>
/// <param name="Rules">The rules as the user wrote them (labels), for the record.</param>
/// <param name="ForcedGcs">When the run forced a garbage collection in the target (only collections whose end was seen).</param>
/// <param name="TraceNote">Why the allocation trace was not recorded, or stopped before the run (review of 3.4).</param>
public sealed record SessionRun(
    Guid Id,
    string? ProcessName,
    int ProcessId,
    double IntervalSeconds,
    string Rules,
    SnapshotKindDto CaptureKind,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    string? Timeline,
    string? Trace,
    IReadOnlyList<Guid> SnapshotIds,
    IReadOnlyList<DateTimeOffset>? ForcedGcs = null,
    string? TraceNote = null)
{
    /// <summary>Members written by a newer HeapScope, kept when this build saves the document.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// What a deletion removed, or would remove when planned (sprint 3.4): the files and their bytes, the files that could not
/// be deleted (each with its reason), and the files left in place on purpose (a session's dumps outside its folder).
/// </summary>
public sealed record Deletion(IReadOnlyList<string> Files, long Bytes, IReadOnlyList<string>? Failed = null, IReadOnlyList<string>? Kept = null)
{
    /// <summary>Whether every file was deleted.</summary>
    public bool Complete => Failed is null or { Count: 0 };
}

/// <summary>A session folder and its document.</summary>
public sealed record Session(string Folder, SessionDocument Document)
{
    /// <summary>Full path of a snapshot recorded in this session.</summary>
    public string PathOf(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Path.IsPathFullyQualified(snapshot.File) ? snapshot.File : Path.Combine(Folder, snapshot.File);
    }
}

/// <summary>A line of the recent sessions list (dotMemory's workspace list).</summary>
public sealed record SessionSummary(string Folder, string Name, DateTimeOffset CreatedUtc, DateTimeOffset LastOpenedUtc, int SnapshotCount, long SizeBytes);

/// <summary>
/// The session snapshot library (sprint 1.1): one folder per session under <see cref="Root"/>, each with a
/// <c>session.json</c>. Captures go into the current session, created on first use or chosen with <see cref="Open"/>.
/// Every change is a read-modify-write of the file on disk under a cross-process lock (<c>session.lock</c>), so
/// concurrent captures and several HeapScope runs never lose records. Damaged or foreign folders are skipped when listing.
/// </summary>
public sealed class SessionLibrary
{
    public const string DocumentName = SessionDocumentFile.DocumentName;
    public const string LockName = "session.lock";

    private readonly TimeProvider _time;

    // _gate guards _currentFolder only and is never held across file work, so CurrentFolder and HasCurrent stay cheap on
    // the UI thread (peer review P1). _currentWork serializes reading or creating the current session; _writeGate
    // serializes this library's updates before they take the cross-process lock.
    private readonly object _gate = new();
    private readonly object _currentWork = new();
    private readonly object _writeGate = new();
    private string? _currentFolder;

    // Sessions something writes into (a capture, a monitoring run) and sessions being deleted (sprint 3.4 review H1):
    // a deletion refuses the first, and the second refuse to start in a session being deleted.
    private readonly object _useGate = new();
    private readonly Dictionary<string, List<string>> _inUse = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deleting = new(StringComparer.OrdinalIgnoreCase);

    public SessionLibrary(string? root = null, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;

        // A relative root is relative to HeapScope's own data folder, never to the current directory.
        Root = string.IsNullOrWhiteSpace(root) ? DefaultRoot
            : Path.IsPathFullyQualified(root) ? Path.GetFullPath(root)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(DefaultRoot)!, root));
    }

    public static string DefaultRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeapScope", "sessions");

    public string Root { get; }

    /// <summary>
    /// The session of this run, created on first use or when its folder or document is gone. A current session whose
    /// document exists but cannot be read fails with <see cref="SessionUnreadableException"/> instead of moving the next
    /// captures into a new session (peer review P1).
    /// </summary>
    public Session Current
    {
        get
        {
            lock (_currentWork)
            {
                if (CurrentFolder is { } folder)
                {
                    var read = SessionDocumentFile.Read(folder);
                    if (read.Status == SessionReadStatus.Read)
                    {
                        return read.Session!;
                    }

                    if (read.Status == SessionReadStatus.Unreadable)
                    {
                        throw new SessionUnreadableException($"The current session {folder} cannot be used: {read.Reason}. Close other programs that hold it, or open or create another session.");
                    }
                }

                var created = CreateSession(null);
                lock (_gate)
                {
                    _currentFolder = created.Folder;
                }

                return created;
            }
        }
    }

    /// <summary>True when a current session exists already (captures will not create a new one).</summary>
    public bool HasCurrent
    {
        get
        {
            lock (_gate)
            {
                return _currentFolder is not null;
            }
        }
    }

    /// <summary>
    /// Creates a new session folder named after the current time and <paramref name="name"/>. The folder is claimed by
    /// creating its <c>session.json</c> exclusively, so two runs starting in the same second get different folders.
    /// </summary>
    public Session CreateSession(string? name)
    {
        var now = _time.GetUtcNow();
        var stamp = now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = string.IsNullOrWhiteSpace(name) ? stamp : $"{stamp}-{Sanitize(name)}";
        var document = new SessionDocument(SessionDocument.CurrentFormatVersion, string.IsNullOrWhiteSpace(name) ? stamp : name.Trim(), now, now, []);
        PrivateFiles.CreateDirectory(Root);
        for (var n = 1; ; n++)
        {
            var folder = Path.Combine(Root, n == 1 ? baseName : $"{baseName}-{n}");
            PrivateFiles.CreateDirectory(folder);
            try
            {
                SessionDocumentFile.CreateNew(folder, document);
                return new Session(folder, document);
            }
            catch (IOException) when (File.Exists(Path.Combine(folder, DocumentName)) && n < 1000)
            {
                // Another run claimed this folder; try the next suffix.
            }
        }
    }

    /// <summary>Makes an existing session the current one (captures go there). Returns null when it is not a session.</summary>
    public Session? Open(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var full = Path.IsPathFullyQualified(folder) ? folder : Path.Combine(Root, folder);
        if (TryRead(full) is not { } session)
        {
            return null;
        }

        lock (_gate)
        {
            _currentFolder = session.Folder;
        }

        return Touch(session.Folder);
    }

    /// <summary>Records a snapshot in the current session and returns the updated session.</summary>
    public Session AddToCurrent(SessionSnapshot snapshot) => AddTo(Current.Folder, snapshot);

    /// <summary>Records a snapshot in the session at <paramref name="folder"/> and returns the updated session.</summary>
    public Session AddTo(string folder, SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Update(folder, document =>
        {
            var file = snapshot.File;
            if (Path.IsPathFullyQualified(file) && string.Equals(Path.GetDirectoryName(file), folder, StringComparison.OrdinalIgnoreCase))
            {
                file = Path.GetFileName(file);
            }

            return document with { Snapshots = [.. document.Snapshots, snapshot with { File = file }] };
        });
    }

    /// <summary>Marks a session as opened now.</summary>
    public Session Touch(string folder) => Update(folder, document => document with { LastOpenedUtc = _time.GetUtcNow() });

    /// <summary>Records a monitoring run, replacing the record with its id (sprint 3.4: written at its start and end).</summary>
    public Session RecordRun(string folder, SessionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Update(folder, document =>
        {
            // A newer build's members on the record survive (forward compatibility, like the document's own).
            var existing = document.Runs?.FirstOrDefault(r => r.Id == run.Id);
            var kept = run.Extra is null && existing?.Extra is { } extra ? run with { Extra = extra } : run;
            return document with { Runs = [.. (document.Runs ?? []).Where(r => r.Id != run.Id), kept] };
        });
    }

    /// <summary>
    /// Marks a session as written into by <paramref name="what"/> (a capture, a monitoring run) until the result is
    /// disposed (sprint 3.4 review H1): it cannot be deleted meanwhile. Fails when the session is being deleted.
    /// </summary>
    public IDisposable MarkInUse(string folder, string what)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var key = Normalize(folder);
        lock (_useGate)
        {
            if (_deleting.Contains(key))
            {
                throw new IOException($"The session {Path.GetFileName(key)} is being deleted.");
            }

            if (!_inUse.TryGetValue(key, out var users))
            {
                _inUse[key] = users = [];
            }

            users.Add(what);
        }

        return new Release(() =>
        {
            lock (_useGate)
            {
                if (_inUse.TryGetValue(key, out var users) && users.Remove(what) && users.Count == 0)
                {
                    _inUse.Remove(key);
                }
            }
        });
    }

    /// <summary>What writes into the session now (for example "a monitoring run"), or null.</summary>
    public string? InUseBy(string folder)
    {
        lock (_useGate)
        {
            return _inUse.TryGetValue(Normalize(folder), out var users) ? string.Join(" and ", users.Distinct()) : null;
        }
    }

    /// <summary>
    /// What deleting a snapshot of a session would remove (sprint 3.4, decision 6): its file, its partial file, and its
    /// index caches and sidecars (next to it and in <paramref name="cacheFallbackRoot"/>). Refused, with the reason, for a
    /// snapshot outside its session folder (a dump captured to a path of the user's choosing stays theirs) or reached
    /// through a link; nothing is deleted here.
    /// </summary>
    public Deletion PlanSnapshotDeletion(string folder, Guid snapshotId, string? cacheFallbackRoot = null)
    {
        var (_, _, path) = SnapshotOf(folder, snapshotId);
        var files = SnapshotFiles(path, cacheFallbackRoot).ToList();
        return new Deletion(files, files.Sum(f => new FileInfo(f).Length));
    }

    /// <summary>
    /// Deletes a snapshot of a session (sprint 3.4, decision 6): what <see cref="PlanSnapshotDeletion"/> lists, then its
    /// entry. The caller has made sure it is not open. A file that cannot be deleted is reported in
    /// <see cref="Deletion.Failed"/>; the entry stays while the dump itself does, so the snapshot is still listed.
    /// </summary>
    public Deletion DeleteSnapshot(string folder, Guid snapshotId, string? cacheFallbackRoot = null)
    {
        var (full, _, path) = SnapshotOf(folder, snapshotId);
        var deletion = DeleteFiles(SnapshotFiles(path, cacheFallbackRoot));

        // The entry stays while its dump does, so the snapshot can be deleted again; a leftover cache alone is reported.
        if (deletion.Complete || !File.Exists(path))
        {
            Update(full, document => document with { Snapshots = [.. document.Snapshots.Where(s => s.Id != snapshotId)] });
        }

        return deletion;
    }

    /// <summary>
    /// What deleting a whole session would remove (sprint 3.4): every file in its folder (links are not followed) and its
    /// snapshots' caches in the fallback root. Dumps the session records outside its folder are kept and listed in
    /// <see cref="Deletion.Kept"/>. Refused, with the reason, for a folder that is not a session of this library, the
    /// current session, or a session something writes into. Nothing is deleted here.
    /// </summary>
    public Deletion PlanSessionDeletion(string folder, string? cacheFallbackRoot = null)
    {
        var session = SessionToDelete(folder);
        var (files, kept) = SessionFiles(session, cacheFallbackRoot);
        return new Deletion(files, files.Sum(f => new FileInfo(f).Length), Kept: kept);
    }

    /// <summary>
    /// Deletes a whole session (sprint 3.4) under its lock: what <see cref="PlanSessionDeletion"/> lists, with
    /// <c>session.json</c> last, so a deletion that fails part way leaves a session that is still listed and can be deleted
    /// again. The caller has made sure none of its snapshots is open.
    /// </summary>
    public Deletion DeleteSession(string folder, string? cacheFallbackRoot = null)
    {
        var full = SessionToDelete(folder).Folder;
        lock (_useGate)
        {
            if (_inUse.TryGetValue(full, out var users))
            {
                throw new InvalidOperationException($"{string.Join(" and ", users.Distinct())} writes into this session; stop it first.");
            }

            if (!_deleting.Add(full))
            {
                throw new InvalidOperationException("This session is being deleted already.");
            }
        }

        try
        {
            Deletion deletion;
            using (AcquireLock(full))
            {
                // Read again under the lock: another HeapScope may have added a snapshot meanwhile.
                var session = TryRead(full) ?? throw new IOException($"{full} is not a readable HeapScope session.");
                var (files, kept) = SessionFiles(session, cacheFallbackRoot);
                var document = Path.Combine(full, DocumentName);
                deletion = DeleteFiles(files.Where(f => !IsFile(f, document) && !IsFile(f, Path.Combine(full, LockName))));
                if (!deletion.Complete)
                {
                    return deletion with { Kept = kept };
                }

                var size = new FileInfo(document).Length;
                File.Delete(document);
                deletion = deletion with { Files = [.. deletion.Files, document], Bytes = deletion.Bytes + size, Kept = kept };
            }

            try
            {
                // Empty folders and the lock file, which closing it removed; links are removed, never followed.
                Directory.Delete(full, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                deletion = deletion with { Failed = [$"{full} ({ex.Message})"] };
            }

            return deletion;
        }
        finally
        {
            lock (_useGate)
            {
                _deleting.Remove(full);
            }
        }
    }

    /// <summary>The session that records the snapshot at <paramref name="path"/>, with its entry, or null (sprint 3.4).</summary>
    public (Session Session, SessionSnapshot Snapshot)? FindSnapshot(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        foreach (var summary in ListRecent(int.MaxValue))
        {
            if (TryRead(summary.Folder) is { } session
                && session.Document.Snapshots.FirstOrDefault(s => string.Equals(Path.GetFullPath(session.PathOf(s)), full, StringComparison.OrdinalIgnoreCase)) is { } snapshot)
            {
                return (session, snapshot);
            }
        }

        return null;
    }

    /// <summary>
    /// Sessions not opened for <paramref name="age"/> (sprint 3.4, "Clean up old sessions"), oldest first, the current one
    /// left out. Nothing is deleted here.
    /// </summary>
    public IReadOnlyList<SessionSummary> ListOlderThan(TimeSpan age)
    {
        var cutoff = _time.GetUtcNow() - age;
        var current = CurrentFolder;
        return [.. ListRecent(int.MaxValue).Where(s => s.LastOpenedUtc < cutoff && !IsFile(s.Folder, current)).OrderBy(s => s.LastOpenedUtc)];
    }

    /// <summary>The current session's folder without reading or creating it (null before the first capture).</summary>
    public string? CurrentFolder
    {
        get
        {
            lock (_gate)
            {
                return _currentFolder is null ? null : Normalize(_currentFolder);
            }
        }
    }

    /// <summary>A session of this library that may be deleted now, or the reason it may not.</summary>
    private Session SessionToDelete(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var full = Normalize(folder);

        // Directly under the root (a separator-safe check: "sessions-old" is not under "sessions"), and not a link.
        if (!IsFile(Path.GetDirectoryName(full), Normalize(Root)) || IsLink(full) || TryRead(full) is not { } session)
        {
            throw new ArgumentException("Only a session of this library can be deleted.", nameof(folder));
        }

        if (IsFile(full, CurrentFolder))
        {
            throw new InvalidOperationException("The current session cannot be deleted while it is current: start a new session first.");
        }

        if (InUseBy(full) is { } user)
        {
            throw new InvalidOperationException($"{char.ToUpperInvariant(user[0])}{user[1..]} writes into this session; stop it first.");
        }

        return new Session(full, session.Document);
    }

    /// <summary>A snapshot of a session that may be deleted: inside its session folder, reached through no link.</summary>
    private (string Folder, SessionSnapshot Snapshot, string Path) SnapshotOf(string folder, Guid snapshotId)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var full = Normalize(folder);
        if (!IsFile(Path.GetDirectoryName(full), Normalize(Root)))
        {
            throw new ArgumentException("Only a snapshot of a session of this library can be deleted.", nameof(folder));
        }

        var session = TryRead(full) ?? throw new IOException($"{full} is not a readable HeapScope session.");
        var snapshot = session.Document.Snapshots.FirstOrDefault(s => s.Id == snapshotId)
            ?? throw new ArgumentException("The snapshot is not in this session.", nameof(snapshotId));
        var path = Path.GetFullPath(session.PathOf(snapshot));
        if (!IsUnder(path, full) || ThroughLink(path, full))
        {
            throw new InvalidOperationException($"{path} is outside its session folder, so HeapScope does not delete it: delete the file yourself.");
        }

        return (full, snapshot, path);
    }

    /// <summary>A session's files: everything in its folder, links not followed, and its own snapshots' fallback caches.</summary>
    private static (List<string> Files, List<string> Kept) SessionFiles(Session session, string? cacheFallbackRoot)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var files = Directory.EnumerateFiles(session.Folder, "*", options).ToList();
        var kept = new List<string>();
        foreach (var snapshot in session.Document.Snapshots)
        {
            var path = Path.GetFullPath(session.PathOf(snapshot));
            if (IsUnder(path, session.Folder) && !ThroughLink(path, session.Folder))
            {
                files.AddRange(IndexCachePaths.Existing(path, cacheFallbackRoot).Where(f => !IsUnder(f, session.Folder)));
            }
            else
            {
                // A dump captured to a path of the user's choosing, with its caches: not HeapScope's to delete.
                kept.Add(path);
            }
        }

        return ([.. files.Distinct(StringComparer.OrdinalIgnoreCase)], kept);
    }

    /// <summary>A snapshot's files: itself, its partial file, and its index caches and sidecars.</summary>
    private static IEnumerable<string> SnapshotFiles(string path, string? cacheFallbackRoot) =>
        new[] { path, path + ".partial" }.Where(File.Exists).Concat(IndexCachePaths.Existing(path, cacheFallbackRoot));

    /// <summary>Deletes the files it can; the others are reported with their reason (sprint 3.4 review H4).</summary>
    private static Deletion DeleteFiles(IEnumerable<string> files)
    {
        var deleted = new List<string>();
        var failed = new List<string>();
        long bytes = 0;
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var size = new FileInfo(file).Length;
                File.Delete(file);
                deleted.Add(file);
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{file} ({ex.Message})");
            }
        }

        return new Deletion(deleted, bytes, failed.Count == 0 ? null : failed);
    }

    private static string Normalize(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private static bool IsFile(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string file, string folder) =>
        Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsLink(string path) =>
        (File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    /// <summary>Whether a folder between <paramref name="folder"/> and <paramref name="path"/> is a link (a junction out).</summary>
    private static bool ThroughLink(string path, string folder)
    {
        for (var dir = Path.GetDirectoryName(path); dir is not null && IsUnder(dir, folder); dir = Path.GetDirectoryName(dir))
        {
            if (IsLink(dir))
            {
                return true;
            }
        }

        return IsLink(folder);
    }

    private sealed class Release(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                release();
            }
        }
    }

    /// <summary>Removes a session folder that holds no snapshots and no files (a capture that failed after creating it).</summary>
    public void DeleteIfEmpty(string folder)
    {
        if (TryRead(folder) is { Document.Snapshots.Count: 0 }
            && Directory.EnumerateFiles(folder).All(f => Path.GetFileName(f) is DocumentName or LockName))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            lock (_gate)
            {
                if (string.Equals(_currentFolder, folder, StringComparison.OrdinalIgnoreCase))
                {
                    _currentFolder = null;
                }
            }
        }
    }

    /// <summary>
    /// Deletes snapshots left half-written (<c>*.dmp.partial</c>, <c>*.gcdump.partial</c> since 1.8, <c>*.nettrace.partial</c> recordings since 2.5) under <paramref name="folder"/> (default: the root) that
    /// are older than <paramref name="minimumAge"/> and no longer held by a writer: the safety net when HeapScope was
    /// killed while a canceled capture was still being written. Returns the number of files removed.
    /// </summary>
    public int SweepPartialDumps(string? folder = null, TimeSpan? minimumAge = null)
    {
        var root = folder ?? Root;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var cutoff = _time.GetUtcNow().UtcDateTime - (minimumAge ?? TimeSpan.FromMinutes(10));
        var removed = 0;
        try
        {
            var search = folder is null ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var file in Directory.EnumerateFiles(root, "*.dmp.partial", search).Concat(Directory.EnumerateFiles(root, "*.gcdump.partial", search)).Concat(Directory.EnumerateFiles(root, "*.nettrace.partial", search)))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still held by a writer; the next sweep tries again.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return removed;
    }

    /// <summary>Reads a session folder; null when it has no readable, well-formed <c>session.json</c>.</summary>
    public static Session? TryRead(string folder) => SessionDocumentFile.Read(folder).Session;

    /// <summary>Reads a session folder and says why it could not be read.</summary>
    public static SessionRead Read(string folder) => SessionDocumentFile.Read(folder);

    /// <summary>Sessions, most recently opened first, with their size on disk.</summary>
    public IReadOnlyList<SessionSummary> ListRecent(int max = 20)
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var result = new List<SessionSummary>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            if (TryRead(folder) is { } session)
            {
                var d = session.Document;
                result.Add(new SessionSummary(folder, d.Name, d.CreatedUtc, d.LastOpenedUtc, d.Snapshots.Count, FolderSize(folder)));
            }
        }

        return [.. result.OrderByDescending(s => s.LastOpenedUtc).ThenByDescending(s => s.CreatedUtc).Take(max)];
    }

    /// <summary>Read-modify-write of a session's document under the in-process gate and the cross-process lock file.</summary>
    private Session Update(string folder, Func<SessionDocument, SessionDocument> change)
    {
        lock (_writeGate)
        {
            using var fileLock = AcquireLock(folder);
            var read = SessionDocumentFile.Read(folder);
            if (read.Status != SessionReadStatus.Read)
            {
                throw new SessionUnreadableException($"{folder} is not a readable HeapScope session: {read.Reason ?? "it has no session.json"}.");
            }

            var updated = new Session(folder, change(read.Session!.Document));
            SessionDocumentFile.Save(updated);
            return updated;
        }
    }

    /// <summary>
    /// Opens <c>session.lock</c> exclusively, retrying for up to 30 seconds while another run holds it. Each holder flushes
    /// the document to disk, which takes long on a busy machine; 10 seconds was too short for many waiters (a gate flake in
    /// sprints 2.3.5 to 2.3.7 lost an update to the deadline, not to a race).
    /// </summary>
    private static FileStream AcquireLock(string folder)
    {
        var path = Path.Combine(folder, LockName);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static long FolderSize(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }

        return builder.Length > 40 ? builder.ToString(0, 40) : builder.ToString();
    }
}
