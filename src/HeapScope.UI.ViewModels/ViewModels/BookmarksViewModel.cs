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
using HeapScope.Application.Bookmarks;
using HeapScope.Application.Snapshots;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.UI.ViewModels;

/// <summary>A bookmark of an open snapshot, as the Bookmarks list shows it (sprint 2.7).</summary>
public sealed partial class BookmarkItem(SnapshotHandle snapshot, Bookmark bookmark) : ObservableObject
{
    [ObservableProperty]
    private Bookmark _bookmark = bookmark;

    public SnapshotHandle Snapshot { get; } = snapshot;

    public ulong Address => Bookmark.Address;

    public string AddressText => string.Create(CultureInfo.InvariantCulture, $"0x{Address:x}");

    public string TypeName => Bookmark.TypeName;

    public string SnapshotTitle => System.IO.Path.GetFileName(Snapshot.Path);

    public BookmarkColor Color => Bookmark.Color;

    /// <summary>The theme brush's key for the color (<c>Brush.Mark.Red</c> and so on).</summary>
    public string ColorKey => "Brush.Mark." + Color;

    partial void OnBookmarkChanged(Bookmark value)
    {
        OnPropertyChanged(nameof(Color));
        OnPropertyChanged(nameof(ColorKey));
    }
}

/// <summary>
/// Object bookmarks (sprint 2.7): marks on objects of any open snapshot, each with one of five colors, listed together
/// and opened with one click. They are kept in a <see cref="BookmarkStore"/> by the snapshot file's identity, so they come
/// back when the file is opened again; without a store (tests) they last for the run. The log counts bookmarks, never
/// addresses or type names.
/// </summary>
public sealed partial class BookmarksViewModel : ObservableObject
{
    private readonly BookmarkStore? _store;
    private readonly INavigator _navigator;
    private readonly ILogger _logger;
    private readonly Dictionary<SnapshotHandle, Task<SnapshotKey?>> _loads = [];

    [ObservableProperty]
    private string? _status;

    public BookmarksViewModel(BookmarkStore? store, INavigator navigator, ILogger<BookmarksViewModel>? logger = null)
    {
        _store = store;
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>The bookmarks of every open snapshot, in the order the snapshots opened and the marks were made.</summary>
    public ObservableCollection<BookmarkItem> Items { get; } = [];

    /// <summary>Reads an opened snapshot's bookmarks (off the dispatcher), adding them to the list; once per snapshot.</summary>
    public Task LoadAsync(SnapshotHandle snapshot) => KeyAsync(snapshot);

    // The snapshot file's key, read with its bookmarks the first time; a toggle right after opening waits for it.
    private Task<SnapshotKey?> KeyAsync(SnapshotHandle snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_loads.TryGetValue(snapshot, out var load))
        {
            _loads[snapshot] = load = ReadAsync(snapshot);
        }

        return load;
    }

    private async Task<SnapshotKey?> ReadAsync(SnapshotHandle snapshot)
    {
        var (key, bookmarks) = await Task.Run(() =>
        {
            var key = SnapshotKey.Of(snapshot.Path);
            return (key, key is null || _store is null ? [] : _store.For(key));
        });
        if (!_loads.ContainsKey(snapshot))
        {
            return key; // closed while reading
        }

        foreach (var bookmark in bookmarks)
        {
            Items.Add(new BookmarkItem(snapshot, bookmark));
        }

        if (bookmarks.Count > 0)
        {
            _logger.LogInformation("Bookmarks: {Count} restored for an opened snapshot", bookmarks.Count);
        }

        return key;
    }

    /// <summary>Drops a closed snapshot's bookmarks from the list (they stay stored).</summary>
    public void Forget(SnapshotHandle snapshot)
    {
        _loads.Remove(snapshot);
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].Snapshot == snapshot)
            {
                Items.RemoveAt(i);
            }
        }
    }

    /// <summary>The bookmark of an object, if it has one.</summary>
    public BookmarkItem? Find(SnapshotHandle snapshot, ulong address) =>
        Items.FirstOrDefault(i => i.Snapshot == snapshot && i.Address == address);

    /// <summary>Bookmarks an object, or removes its bookmark when it has one; true when it is bookmarked afterwards.</summary>
    public async Task<bool> ToggleAsync(SnapshotHandle snapshot, ulong address, string? typeName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (address == 0)
        {
            return false;
        }

        var key = await KeyAsync(snapshot);
        if (Find(snapshot, address) is { } existing)
        {
            await RemoveAsync(existing);
            return false;
        }

        if (key is null)
        {
            Status = "This snapshot's file cannot be read, so its objects cannot be bookmarked.";
            return false;
        }

        var bookmark = new Bookmark(key, address, string.IsNullOrEmpty(typeName) ? "object" : typeName, NextColor(), DateTimeOffset.UtcNow);
        if (!await SaveAsync(() => _store?.Set(bookmark)))
        {
            return false;
        }

        Items.Add(new BookmarkItem(snapshot, bookmark));
        _logger.LogInformation("Bookmarks: added one ({Count} in the open snapshots)", Items.Count);
        Status = null;
        return true;
    }

    [RelayCommand]
    private void Open(BookmarkItem? item)
    {
        if (item is not null)
        {
            _navigator.Navigate(NavigationTarget.Object(item.Snapshot, item.Address));
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(BookmarkItem? item)
    {
        if (item is null || !_loads.TryGetValue(item.Snapshot, out var load) || await load is not { } key)
        {
            return;
        }

        if (await SaveAsync(() => _store?.Remove(key, item.Address)))
        {
            Items.Remove(item);
            _logger.LogInformation("Bookmarks: removed one ({Count} in the open snapshots)", Items.Count);
        }
    }

    /// <summary>Gives a bookmark the next of the five colors.</summary>
    [RelayCommand]
    private async Task CycleColorAsync(BookmarkItem? item)
    {
        if (item is null)
        {
            return;
        }

        var colors = Enum.GetValues<BookmarkColor>();
        var changed = item.Bookmark with { Color = colors[((int)item.Color + 1) % colors.Length] };
        if (await SaveAsync(() => _store?.Set(changed)))
        {
            item.Bookmark = changed;
        }
    }

    // New marks take the colors in turn, so marks made one after another are told apart.
    private BookmarkColor NextColor()
    {
        var colors = Enum.GetValues<BookmarkColor>();
        return colors[Items.Count % colors.Length];
    }

    private async Task<bool> SaveAsync(Action save)
    {
        try
        {
            await Task.Run(save);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Bookmarks could not be saved: {Error}", ex.GetType().Name);
            Status = "Bookmarks could not be saved: " + ex.Message;
            return false;
        }
    }
}
