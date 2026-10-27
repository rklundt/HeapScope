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

/// <summary>
/// Object bookmarks kept across runs (sprint 2.7) in <c>bookmarks.json</c> in the app's data folder (beside the session
/// library; tests pass their own folder). The file is read once and rewritten whole, through a temporary file, on each
/// change. Thread-safe; callers keep file access off the dispatcher.
/// </summary>
public sealed class BookmarkStore
{
    public const string FileName = "bookmarks.json";

    /// <summary>The format written; a newer format is read as empty and never overwritten.</summary>
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private List<Bookmark>? _bookmarks;
    private bool _readOnly;

    /// <param name="folder">The folder of the file; null for HeapScope's data folder.</param>
    public BookmarkStore(string? folder = null)
    {
        Folder = folder ?? DefaultFolder;
    }

    public static string DefaultFolder { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeapScope");

    public string Folder { get; }

    public string Path => System.IO.Path.Combine(Folder, FileName);

    /// <summary>The bookmarks of the snapshot file <paramref name="key"/>, oldest first.</summary>
    public IReadOnlyList<Bookmark> For(SnapshotKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            return [.. Load().Where(b => b.Snapshot.Matches(key))];
        }
    }

    /// <summary>Adds or replaces the bookmark of an object (one per address and snapshot), then saves.</summary>
    public void Set(Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        lock (_gate)
        {
            var all = Load();
            var index = all.FindIndex(b => b.Address == bookmark.Address && b.Snapshot.Matches(bookmark.Snapshot));
            if (index >= 0)
            {
                all[index] = bookmark;
            }
            else
            {
                all.Add(bookmark);
            }

            Save(all);
        }
    }

    /// <summary>Removes the bookmark of an object; false when there was none.</summary>
    public bool Remove(SnapshotKey key, ulong address)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var all = Load();
            if (all.RemoveAll(b => b.Address == address && b.Snapshot.Matches(key)) == 0)
            {
                return false;
            }

            Save(all);
            return true;
        }
    }

    private List<Bookmark> Load()
    {
        if (_bookmarks is not null)
        {
            return _bookmarks;
        }

        _bookmarks = [];
        try
        {
            if (File.Exists(Path))
            {
                var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(Path), s_json);
                if (document is { FormatVersion: > FormatVersion })
                {
                    _readOnly = true;
                }
                else if (document?.Bookmarks is { } bookmarks)
                {
                    _bookmarks.AddRange(bookmarks.Where(b => b?.Snapshot?.Path is not null && b.TypeName is not null));
                }
            }
        }
        catch (JsonException)
        {
            // A damaged file is set aside rather than lost; the next save writes a new one.
            try
            {
                File.Move(Path, Path + ".damaged", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _readOnly = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: never overwrite what could not be read.
            _readOnly = true;
        }

        return _bookmarks;
    }

    private void Save(List<Bookmark> all)
    {
        if (_readOnly)
        {
            throw new IOException("The bookmarks file is from a newer HeapScope or could not be read; bookmarks are not saved.");
        }

        Directory.CreateDirectory(Folder);
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(FormatVersion, all), s_json));
        File.Move(temporary, Path, overwrite: true);
    }

    private sealed record Document(int FormatVersion, List<Bookmark> Bookmarks);
}
