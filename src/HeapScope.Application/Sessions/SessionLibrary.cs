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
/// <param name="ForcedGcs">When the run forced a garbage collection in the target.</param>
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
    IReadOnlyList<DateTimeOffset>? ForcedGcs = null)
{
    /// <summary>Members written by a newer HeapScope, kept when this build saves the document.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>What a deletion removed (sprint 3.4): the files and their bytes.</summary>
public sealed record Deletion(IReadOnlyList<string> Files, long Bytes);

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
    public const string DocumentName = "session.json";
    public const string LockName = "session.lock";

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private string? _currentFolder;

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

    /// <summary>The session of this run, created on first use.</summary>
    public Session Current
    {
        get
        {
            lock (_gate)
            {
                if (_currentFolder is not null && TryRead(_currentFolder) is { } current)
                {
                    return current;
                }

                var created = CreateSession(null);
                _currentFolder = created.Folder;
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
                using (var stream = PrivateFiles.Open(Path.Combine(folder, DocumentName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, document, s_json);
                    stream.Flush(flushToDisk: true);
                }

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
    /// Deletes a snapshot of a session (sprint 3.4, decision 6): its file, its partial and DAC leftovers, its index caches
    /// and sidecars (next to it and in <paramref name="cacheFallbackRoot"/>), and its entry. The caller has made sure it is
    /// not open. A file that cannot be deleted fails the call after the others are gone; the entry stays then.
    /// </summary>
    public Deletion DeleteSnapshot(string folder, Guid snapshotId, string? cacheFallbackRoot = null)
    {
        var session = TryRead(folder) ?? throw new IOException($"{folder} is not a readable HeapScope session.");
        var snapshot = session.Document.Snapshots.FirstOrDefault(s => s.Id == snapshotId)
            ?? throw new ArgumentException("The snapshot is not in this session.", nameof(snapshotId));
        var deletion = DeleteFiles(SnapshotFiles(session.PathOf(snapshot), cacheFallbackRoot));
        Update(folder, document => document with { Snapshots = [.. document.Snapshots.Where(s => s.Id != snapshotId)] });
        return deletion;
    }

    /// <summary>
    /// Deletes a whole session (sprint 3.4): its snapshots' caches in the fallback root, then its folder. The caller has made
    /// sure none of its snapshots is open and nothing captures into it.
    /// </summary>
    public Deletion DeleteSession(string folder, string? cacheFallbackRoot = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var full = Path.GetFullPath(folder);
        if (!full.StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase) || TryRead(full) is not { } session)
        {
            throw new ArgumentException("Only a session of this library can be deleted.", nameof(folder));
        }

        lock (_gate)
        {
            if (HasCurrent && string.Equals(Path.GetFullPath(Current.Folder), full, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The current session cannot be deleted while it is current: start a new session first.");
            }
        }

        var files = session.Document.Snapshots.SelectMany(s => IndexCachePaths.Existing(session.PathOf(s), cacheFallbackRoot)).Where(f => !IsUnder(f, full)).ToList();
        var outside = DeleteFiles(files);
        var inside = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).ToList();
        var bytes = inside.Sum(f => new FileInfo(f).Length);
        Directory.Delete(full, recursive: true);
        return new Deletion([.. outside.Files, .. inside], outside.Bytes + bytes);
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

    /// <summary>The bytes a snapshot's deletion would free: its file and its caches (sprint 3.4, for the confirmation).</summary>
    public static long SizeOf(string path, string? cacheFallbackRoot = null) =>
        SnapshotFiles(path, cacheFallbackRoot).Sum(f => new FileInfo(f).Length);

    /// <summary>
    /// Sessions not opened for <paramref name="age"/> (sprint 3.4, "Clean up old sessions"), oldest first, the current one
    /// left out. Nothing is deleted here.
    /// </summary>
    public IReadOnlyList<SessionSummary> ListOlderThan(TimeSpan age)
    {
        var cutoff = _time.GetUtcNow() - age;
        var current = HasCurrent ? Path.GetFullPath(Current.Folder) : null;
        return [.. ListRecent(int.MaxValue).Where(s => s.LastOpenedUtc < cutoff && !string.Equals(Path.GetFullPath(s.Folder), current, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.LastOpenedUtc)];
    }

    /// <summary>A snapshot's files: itself, its partial file, and its index caches and sidecars.</summary>
    private static IEnumerable<string> SnapshotFiles(string path, string? cacheFallbackRoot) =>
        new[] { path, path + ".partial" }.Where(File.Exists).Concat(IndexCachePaths.Existing(path, cacheFallbackRoot));

    private static Deletion DeleteFiles(IEnumerable<string> files)
    {
        var deleted = new List<string>();
        long bytes = 0;
        List<Exception>? failures = null;
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
                (failures ??= []).Add(ex);
            }
        }

        return failures is null ? new Deletion(deleted, bytes) : throw new IOException($"{failures.Count} files could not be deleted (they may be in use): {failures[0].Message}", failures[0]);
    }

    private static bool IsUnder(string file, string folder) =>
        Path.GetFullPath(file).StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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
    public static Session? TryRead(string folder)
    {
        try
        {
            var path = Path.Combine(folder, DocumentName);
            if (!File.Exists(path))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<SessionDocument>(File.ReadAllBytes(path), s_json);
            return document is { FormatVersion: SessionDocument.CurrentFormatVersion, Name: not null, Snapshots: not null }
                && document.Snapshots.All(s => s is { File.Length: > 0 })
                ? new Session(folder, document)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

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
        lock (_gate)
        {
            using var fileLock = AcquireLock(folder);
            var current = TryRead(folder) ?? throw new IOException($"{folder} is not a readable HeapScope session.");
            var updated = new Session(folder, change(current.Document));
            Save(updated);
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

    private static void Save(Session session)
    {
        var path = Path.Combine(session.Folder, DocumentName);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        using (var stream = PrivateFiles.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, session.Document, s_json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
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
