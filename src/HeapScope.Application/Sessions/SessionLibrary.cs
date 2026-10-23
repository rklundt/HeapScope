using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HeapScope.Contracts;

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
    IReadOnlyList<SessionSnapshot> Snapshots)
{
    public const int CurrentFormatVersion = 1;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
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
        Directory.CreateDirectory(Root);
        for (var n = 1; ; n++)
        {
            var folder = Path.Combine(Root, n == 1 ? baseName : $"{baseName}-{n}");
            Directory.CreateDirectory(folder);
            try
            {
                using (var stream = new FileStream(Path.Combine(folder, DocumentName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
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
    /// Deletes dumps left half-written (<c>*.dmp.partial</c>) under <paramref name="folder"/> (default: the root) that
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
            foreach (var file in Directory.EnumerateFiles(root, "*.dmp.partial", folder is null ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
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

    /// <summary>Opens <c>session.lock</c> exclusively, retrying for up to 10 seconds while another run holds it.</summary>
    private static FileStream AcquireLock(string folder)
    {
        var path = Path.Combine(folder, LockName);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
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
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
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
