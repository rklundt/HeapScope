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
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeapScope.Application.Bookmarks;

/// <summary>The colors a bookmark can have (sprint 2.7); the UI maps each to a theme brush.</summary>
public enum BookmarkColor
{
    Red = 0,
    Orange = 1,
    Green = 2,
    Blue = 3,
    Purple = 4,
}

/// <summary>
/// Which snapshot file a bookmark belongs to: its full path, size and last write time, so a file replaced at the same
/// path (a new capture) does not inherit the old one's marks.
/// </summary>
public sealed record SnapshotKey(string Path, long Length, DateTime WriteTimeUtc)
{
    /// <summary>The key of the file at <paramref name="path"/>; null when it cannot be read.</summary>
    public static SnapshotKey? Of(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? new SnapshotKey(file.FullName, file.Length, file.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public bool Matches(SnapshotKey other) =>
        other is not null && Length == other.Length && WriteTimeUtc == other.WriteTimeUtc
        && string.Equals(Path, other.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>One marked object: where, which, and how it is shown. No field values or string text (ADR-026).</summary>
public sealed record Bookmark(SnapshotKey Snapshot, ulong Address, string TypeName, BookmarkColor Color, DateTimeOffset CreatedUtc);

/// <summary>What reading the bookmarks file found (2.7 review: problems are told, not silent).</summary>
public enum BookmarkFileProblem
{
    None = 0,

    /// <summary>Written by a newer HeapScope: read as empty and never overwritten.</summary>
    NewerFormat = 1,

    /// <summary>Not valid JSON: set aside as <c>bookmarks.json.damaged-*</c>; the next save starts a new file.</summary>
    Damaged = 2,

    /// <summary>Could not be read (locked, no access): never overwritten.</summary>
    Unreadable = 3,
}

/// <summary>
/// Object bookmarks kept across runs (sprint 2.7) in <c>bookmarks.json</c> in the app's data folder (beside the session
/// library; tests pass their own folder). Every read reads the file; every change re-reads it, changes it and writes it
/// whole through a temporary file under a lock file, so two HeapScope windows never overwrite each other's marks (review
/// H3). Thread-safe; callers keep file access off the dispatcher.
/// </summary>
public sealed class BookmarkStore
{
    public const string FileName = "bookmarks.json";

    /// <summary>The format written; a newer format is read as empty and never overwritten.</summary>
    public const int FormatVersion = 1;

    /// <summary>How long a change waits for another window's change to finish.</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();

    /// <param name="folder">The folder of the file; null for HeapScope's data folder.</param>
    public BookmarkStore(string? folder = null)
    {
        Folder = folder ?? DefaultFolder;
    }

    public static string DefaultFolder { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeapScope");

    public string Folder { get; }

    public string Path => System.IO.Path.Combine(Folder, FileName);

    /// <summary>What the last read found.</summary>
    public BookmarkFileProblem Problem { get; private set; }

    /// <summary>The bookmarks of the snapshot file <paramref name="key"/>, oldest first.</summary>
    public IReadOnlyList<Bookmark> For(SnapshotKey key) => For(key, out _);

    /// <summary>The bookmarks of <paramref name="key"/>, and what this read found in the file (not another read's).</summary>
    public IReadOnlyList<Bookmark> For(SnapshotKey key, out BookmarkFileProblem problem)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var bookmarks = Read();
            problem = Problem;
            return [.. bookmarks.Where(b => b.Snapshot.Matches(key))];
        }
    }

    /// <summary>Adds or replaces the bookmark of an object (one per address and snapshot), then saves.</summary>
    public void Set(Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        Change(all =>
        {
            var index = all.FindIndex(b => b.Address == bookmark.Address && b.Snapshot.Matches(bookmark.Snapshot));
            if (index >= 0)
            {
                all[index] = bookmark;
            }
            else
            {
                all.Add(bookmark);
            }

            return true;
        });
    }

    /// <summary>Removes the bookmark of an object; false when there was none.</summary>
    public bool Remove(SnapshotKey key, ulong address)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Change(all => all.RemoveAll(b => b.Address == address && b.Snapshot.Matches(key)) > 0);
    }

    // Under the lock file: read what is on disk now, change it, write it back (nothing when the change changed nothing).
    private bool Change(Func<List<Bookmark>, bool> change)
    {
        lock (_gate)
        {
            HeapScope.Core.PrivateFiles.CreateDirectory(Folder);
            using var fileLock = AcquireLock();
            var all = Read();
            if (Problem is BookmarkFileProblem.NewerFormat or BookmarkFileProblem.Unreadable)
            {
                throw new IOException(Problem == BookmarkFileProblem.NewerFormat
                    ? "The bookmarks file was written by a newer HeapScope; update HeapScope to change bookmarks."
                    : "The bookmarks file could not be read, so it is not overwritten; check that no other program holds it.");
            }

            if (!change(all))
            {
                return false;
            }

            Save(all);
            return true;
        }
    }

    private FileStream AcquireLock()
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                // The lock file stays: deleting it on close would let two processes lock two files of one name on
                // Linux and macOS, where the lock is on the open file (review).
                return HeapScope.Core.PrivateFiles.Open(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (IOException ex)
            {
                throw new IOException("Another HeapScope window is saving bookmarks; try again in a moment.", ex);
            }
        }
    }

    private List<Bookmark> Read()
    {
        Problem = BookmarkFileProblem.None;
        var bookmarks = new List<Bookmark>();
        try
        {
            if (!File.Exists(Path))
            {
                return bookmarks;
            }

            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(Path), s_json);
            if (document is { FormatVersion: > FormatVersion })
            {
                Problem = BookmarkFileProblem.NewerFormat;
            }
            else if (document?.Bookmarks is { } stored)
            {
                bookmarks.AddRange(stored.Where(b => b?.Snapshot?.Path is not null && b.TypeName is not null));
            }
        }
        catch (JsonException)
        {
            // A damaged file is set aside, each with its own time, rather than lost; the next save writes a new one.
            Problem = BookmarkFileProblem.Damaged;
            try
            {
                File.Move(Path, string.Create(CultureInfo.InvariantCulture, $"{Path}.damaged-{DateTime.UtcNow:yyyyMMddHHmmss}"), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Problem = BookmarkFileProblem.Unreadable;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = BookmarkFileProblem.Unreadable;
        }

        return bookmarks;
    }

    private void Save(List<Bookmark> all)
    {
        // Owner-only off Windows, like HeapScope's other files (it names dump paths); flushed before the move, so a crash
        // leaves the old file or the new one, never a short one.
        var temporary = string.Create(CultureInfo.InvariantCulture, $"{Path}.{Environment.ProcessId}.tmp");
        using (var stream = HeapScope.Core.PrivateFiles.Open(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, new Document(FormatVersion, all), s_json);
            stream.Flush(flushToDisk: true);
        }

        // A reader in another window can hold the file for a moment (Windows refuses the replace then): try again briefly.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporary, Path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(25);
            }
        }
    }

    private sealed record Document(int FormatVersion, List<Bookmark> Bookmarks);
}
