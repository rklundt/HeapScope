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
using HeapScope.Application.Bookmarks;
using HeapScope.Application.Sessions;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;


/// <summary>An open snapshot as shown in the snapshot list and its overview.</summary>
public sealed class SnapshotItemViewModel(SnapshotHandle handle)
{
    public SnapshotHandle Handle { get; } = handle ?? throw new ArgumentNullException(nameof(handle));

    public string Title => System.IO.Path.GetFileName(Handle.Path);

    public string Path => Handle.Path;

    public string Process
    {
        get
        {
            var d = Handle.Descriptor;
            var pid = d.ProcessId is { } id ? string.Create(CultureInfo.CurrentCulture, $" (pid {id})") : string.Empty;
            var captured = d.CapturedAtUtc is { } at ? ", captured " + at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : string.Empty;
            return $"{d.ProcessName ?? "Unknown process"}{pid}, .NET {d.RuntimeVersion ?? "?"}{captured}";
        }
    }

    public string Objects => string.Create(CultureInfo.CurrentCulture, $"{Handle.Descriptor.ObjectCount:N0}");

    public string Size => Format.Bytes((long)Handle.Descriptor.TotalSize);

    public string Types => string.Create(CultureInfo.CurrentCulture, $"{Handle.Descriptor.TypeCount:N0}");

    public string References => string.Create(CultureInfo.CurrentCulture, $"{Handle.Descriptor.ReferenceCount:N0}");

    public string Roots => string.Create(CultureInfo.CurrentCulture, $"{Handle.Descriptor.RootCount:N0}");

    public string Source
    {
        get
        {
            var d = Handle.Descriptor;
            var from = d.FromCache ? "index cache" : "dump";
            var mode = d.Mode == SnapshotMemoryMode.Mapped ? "memory-mapped" : "in memory";
            return string.Create(CultureInfo.CurrentCulture, $"Opened from the {from} in {d.OpenSeconds:N1} s, {mode}");
        }
    }

    public IReadOnlyList<string> Notes => Handle.Descriptor.Notes;

    public bool HasNotes => Handle.Descriptor.Notes.Count > 0;
}

/// <summary>
/// The Snapshots page: open a dump (sprint 1.2), see the open snapshots, and the selected one's overview tiles, heap
/// kinds bar and types grid (sprint 1.3, dotMemory's types view).
/// </summary>
public sealed partial class SnapshotsViewModel : ObservableObject, INavigator, IDisposable
{
    private readonly SnapshotSession _session;
    private readonly WorkerOperations _operations;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUserInteraction _interaction;

    [ObservableProperty]
    private SnapshotItemViewModel? _selected;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isOpening;

    /// <summary>The detail tab's index, for the view's tab control; code uses <see cref="Tab"/>.</summary>
    [ObservableProperty]
    private int _selectedTab;

    private readonly SessionLibrary? _library;
    private readonly ILogger? _logger;

    public SnapshotsViewModel(
        SnapshotSession session,
        WorkerOperations operations,
        IUiDispatcher dispatcher,
        IUserInteraction interaction,
        ILoggerFactory? loggers = null,
        BookmarkStore? bookmarks = null,
        SessionLibrary? library = null)
    {
        _library = library;
        _logger = loggers?.CreateLogger<SnapshotsViewModel>();
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _session.SnapshotsChanged += OnSnapshotsChanged;
        Types = new TypeSummaryViewModel(session, dispatcher, loggers?.CreateLogger<TypeSummaryViewModel>(), this);
        Navigator = new NavigatorViewModel(session, Types, this, loggers);
        Navigator.StepShown += OnStepShown;
        Bookmarks = new BookmarksViewModel(bookmarks, this, loggers?.CreateLogger<BookmarksViewModel>());
        Compare = new CompareViewModel(session, Snapshots, dispatcher, loggers?.CreateLogger<CompareViewModel>(), this);
        Inspections = new InspectionsViewModel(session, loggers?.CreateLogger<InspectionsViewModel>(), this);
        Retainers = new RetainersViewModel(session, dispatcher, loggers?.CreateLogger<RetainersViewModel>(), this, loggers?.CreateLogger<RetainedBySetViewModel>());
    }

    /// <summary>The detail tab (2.3.7 review M8: named, not numbered).</summary>
    public SnapshotTabs Tab
    {
        get => (SnapshotTabs)SelectedTab;
        set => SelectedTab = (int)value;
    }

    /// <summary>
    /// Follows a link from anywhere on the page (sprint 2.4, 2.3.7 review M8): selects the target's snapshot, picks the tab
    /// and opens the subject.
    /// </summary>
    public void Navigate(NavigationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Selected?.Handle != target.Snapshot && Snapshots.FirstOrDefault(s => s.Handle == target.Snapshot) is { } item)
        {
            Selected = item;
        }

        if (target.Kind is NavigationKind.Retainers or NavigationKind.RetainedBySet)
        {
            // The target first, so showing the tab queries what it asks for rather than the biggest retainers. The link is a
            // step of the analysis path (sprint 2.7), replayed when it is chosen again.
            Retainers.Navigate(target);
            Navigator.Record(target);
            Tab = SnapshotTabs.Retainers;
            return;
        }

        Tab = SnapshotTabs.Snapshot;
        Navigator.Navigate(target);
    }

    public ObservableCollection<SnapshotItemViewModel> Snapshots { get; } = [];

    /// <summary>The types grid of the selected snapshot.</summary>
    public TypeSummaryViewModel Types { get; }

    /// <summary>The Snapshot tab's subjects: the types grid, instances and objects, with back and forward (sprint 1.5).</summary>
    public NavigatorViewModel Navigator { get; }

    /// <summary>Bookmarked objects of every open snapshot (sprint 2.7).</summary>
    public BookmarksViewModel Bookmarks { get; }

    /// <summary>
    /// Bookmarks <paramref name="row"/>'s object when it opens one, else the object shown on the Snapshot tab, or removes
    /// its bookmark (Command+D, sprint 2.7); false when there was nothing to bookmark.
    /// </summary>
    public async Task<bool> ToggleBookmarkAsync(IObjectLink? row = null)
    {
        if (Selected is not { } selected)
        {
            return false;
        }

        if (row is { LinkAddress: not 0 and var address })
        {
            await Bookmarks.ToggleAsync(selected.Handle, address, row.LinkTypeName);
            return true;
        }

        if (Tab == SnapshotTabs.Snapshot && Navigator.Current is ObjectViewModel { Address: not 0 } shown)
        {
            await Bookmarks.ToggleAsync(shown.Snapshot, shown.Address, shown.TypeName);
            return true;
        }

        return false;
    }

    /// <summary>Bookmarks the object shown on the Snapshot tab, or removes its bookmark (the object view's button).</summary>
    [RelayCommand]
    private Task ToggleBookmark() => ToggleBookmarkAsync();

    /// <summary>The comparison of two open snapshots (sprint 1.4).</summary>
    public CompareViewModel Compare { get; }

    /// <summary>The selected snapshot's inspections (sprint 1.6); they run when their tab is shown.</summary>
    public InspectionsViewModel Inspections { get; }

    /// <summary>The selected snapshot's dominator tree, icicle and set retention (sprint 2.4); queried when the tab is shown.</summary>
    public RetainersViewModel Retainers { get; }

    public void Dispose()
    {
        _session.SnapshotsChanged -= OnSnapshotsChanged;
        Navigator.StepShown -= OnStepShown;
    }

    /// <summary>
    /// Opens <paramref name="path"/>: asks first when a first load may not fit in available memory (Application decides,
    /// from the worker's look at the file and its index cache), then opens it as a tracked job and selects it. Nothing
    /// here touches the file system, so the dispatcher never waits on a slow disk.
    /// </summary>
    public async Task OpenPathAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        IsOpening = true;
        Status = $"Opening {System.IO.Path.GetFileName(path)}...";
        try
        {
            if (await _session.GetOpenWarningAsync(path, rebuild: false, CancellationToken.None) is { } warning
                && !await _interaction.ConfirmAsync("Not enough free memory", warning + "\n\nOpen the dump anyway?"))
            {
                Status = "Open canceled.";
                return;
            }

            var handle = await _session.StartOpen(path).Result;
            Refresh();
            Selected = Snapshots.FirstOrDefault(s => s.Handle == handle);
            Status = null;
        }
        catch (OperationCanceledException)
        {
            Status = "Open canceled.";
        }
        catch (Exception ex) when (ex is WorkerException)
        {
            Status = ex.Message;
        }
        finally
        {
            IsOpening = false;
        }
    }

    [RelayCommand]
    private async Task OpenDumpAsync()
    {
        if (await _interaction.PickDumpFileAsync() is { } path)
        {
            await OpenPathAsync(path);
        }
    }

    [RelayCommand]
    private async Task CloseAsync(SnapshotItemViewModel? item)
    {
        item ??= Selected;
        if (item is null)
        {
            return;
        }

        try
        {
            await _session.CloseAsync(item.Handle, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WorkerException)
        {
            Status = ex.Message;
        }

        Refresh();
    }

    /// <summary>
    /// Closes a snapshot and deletes its dump with its index caches (sprint 3.4, decision 6), after the user confirms what
    /// goes and its size. Only a snapshot recorded in a HeapScope session is deleted: other dumps are the user's files.
    /// </summary>
    [RelayCommand]
    private async Task CloseAndDeleteAsync(SnapshotItemViewModel? item)
    {
        item ??= Selected;
        if (item is null || _library is null)
        {
            return;
        }

        var path = item.Handle.Path;
        var found = await Task.Run(() => _library.FindSnapshot(path));
        if (found is not { } entry)
        {
            _logger?.LogInformation("Deleting snapshot {Path} refused: it is not in a HeapScope session", path);
            Status = "This dump is not in a HeapScope session, so HeapScope does not delete it: close it and delete the file yourself.";
            return;
        }

        Deletion plan;
        try
        {
            plan = await Task.Run(() => _library.PlanSnapshotDeletion(entry.Session.Folder, entry.Snapshot.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _logger?.LogInformation("Deleting snapshot {Path} refused: {Reason}", path, ex.Message);
            Status = ex.Message;
            return;
        }

        var question = string.Create(System.Globalization.CultureInfo.CurrentCulture, $"Close {item.Title} and delete it with its index caches, {plan.Files.Count} files ({Format.Bytes(plan.Bytes)})? It is removed from the session {entry.Session.Document.Name}. This cannot be undone.");
        var yes = await _interaction.ConfirmAsync("Delete snapshot", question + Environment.NewLine + DeletionText.Files(plan));
        _logger?.LogInformation("Asked to delete snapshot {SnapshotId} ({Path}, {Files} files, {Bytes} bytes); the user answered {Answer}", entry.Snapshot.Id, path, plan.Files.Count, plan.Bytes, yes ? "yes" : "no");
        if (!yes)
        {
            return;
        }

        try
        {
            // Closed first: the worker unmaps the cache, so the files can go.
            await _session.CloseAsync(item.Handle, CancellationToken.None);
            Refresh();
            var deletion = await Task.Run(() => _library.DeleteSnapshot(entry.Session.Folder, entry.Snapshot.Id));
            DeletionText.Log(_logger, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"snapshot {entry.Snapshot.Id} of session {entry.Session.Folder}"), deletion);
            Status = deletion.Complete
                ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $"Deleted {item.Title} and its caches: {Format.Bytes(deletion.Bytes)} freed.")
                : string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{deletion.Failed!.Count} files of {item.Title} could not be deleted, usually because another program has them open: {string.Join("; ", deletion.Failed)}. {(File.Exists(path) ? "The snapshot stays in its session: open it and use Close and delete again once they are free, or delete them yourself." : "The snapshot is removed from its session; delete these files yourself once they are free.")}");
        }
        catch (Exception ex) when (ex is WorkerException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _logger?.LogWarning(ex, "Deleting snapshot {Path} failed", path);
            Status = "The snapshot could not be deleted: " + ex.Message;
        }
    }

    partial void OnSelectedChanged(SnapshotItemViewModel? value)
    {
        // Each snapshot keeps its analysis path while it is open (sprint 2.7).
        Navigator.Show(value?.Handle);
        Types.Show(value?.Handle);
        Inspections.Show(value?.Handle);
        Retainers.Show(value?.Handle);
    }

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(Tab));
        Inspections.IsActive = value == (int)SnapshotTabs.Inspections;
        Retainers.IsActive = value == (int)SnapshotTabs.Retainers;
    }

    private void OnSnapshotsChanged(object? sender, EventArgs e) => _dispatcher.Post(Refresh);

    // Back, Forward or the sidebar showed a step: show its tab, replaying a Retainers link (sprint 2.7).
    private void OnStepShown(object? sender, AnalysisStep step)
    {
        if (step.Target is { } target)
        {
            Retainers.Navigate(target);
            Tab = SnapshotTabs.Retainers;
        }
        else
        {
            Tab = SnapshotTabs.Snapshot;
        }
    }

    private void Refresh()
    {
        var handles = _session.Snapshots;
        for (var i = Snapshots.Count - 1; i >= 0; i--)
        {
            if (!handles.Contains(Snapshots[i].Handle))
            {
                Navigator.Forget(Snapshots[i].Handle);
                Bookmarks.Forget(Snapshots[i].Handle);
                Snapshots.RemoveAt(i);
            }
        }

        foreach (var handle in handles)
        {
            if (!Snapshots.Any(s => s.Handle == handle))
            {
                Snapshots.Add(new SnapshotItemViewModel(handle));
                _ = Bookmarks.LoadAsync(handle);
            }
        }

        if (Selected is not null && !Snapshots.Contains(Selected))
        {
            Selected = Snapshots.FirstOrDefault();
        }

        // Another snapshot opened or closed: the growth detectors' baseline choices follow (2.6 review M12).
        Inspections.RefreshBaselines();
    }
}

/// <summary>Display formatting shared by view models.</summary>
public static class Format
{
    /// <summary>Humanized size for cards and summaries (grids show exact bytes).</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 30):N2} GB"),
        >= 1L << 20 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 20):N1} MB"),
        >= 1L << 10 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 10):N1} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes:N0} bytes"),
    };

    /// <summary>A humanized size with its sign (+1.5 MB, -200 bytes, 0 bytes), for deltas.</summary>
    public static string SignedBytes(long bytes) => bytes switch
    {
        > 0 => "+" + Bytes(bytes),
        < 0 => "-" + Bytes(bytes == long.MinValue ? long.MaxValue : -bytes),
        _ => Bytes(0),
    };
}
