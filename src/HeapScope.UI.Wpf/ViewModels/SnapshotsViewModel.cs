using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.UI.Wpf.Services;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.Wpf.ViewModels;


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
public sealed partial class SnapshotsViewModel : ObservableObject, IDisposable
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

    /// <summary>The detail tab: 0 the selected snapshot, 1 the comparison.</summary>
    [ObservableProperty]
    private int _selectedTab;

    public SnapshotsViewModel(
        SnapshotSession session,
        WorkerOperations operations,
        IUiDispatcher dispatcher,
        IUserInteraction interaction,
        ILoggerFactory? loggers = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _session.SnapshotsChanged += OnSnapshotsChanged;
        Types = new TypeSummaryViewModel(session, loggers?.CreateLogger<TypeSummaryViewModel>());
        Navigator = new NavigatorViewModel(session, Types, loggers);
        Compare = new CompareViewModel(session, Snapshots, loggers?.CreateLogger<CompareViewModel>());
        Compare.OpenRowCommand = OpenCompareRowCommand;
    }

    public ObservableCollection<SnapshotItemViewModel> Snapshots { get; } = [];

    /// <summary>The types grid of the selected snapshot.</summary>
    public TypeSummaryViewModel Types { get; }

    /// <summary>The Snapshot tab's subjects: the types grid, instances and objects, with back and forward (sprint 1.5).</summary>
    public NavigatorViewModel Navigator { get; }

    /// <summary>The comparison of two open snapshots (sprint 1.4).</summary>
    public CompareViewModel Compare { get; }

    public void Dispose() => _session.SnapshotsChanged -= OnSnapshotsChanged;

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
                && !_interaction.Confirm("Not enough free memory", warning + "\n\nOpen the dump anyway?"))
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
        catch (Exception ex) when (ex is WorkerStartException or WorkerCrashedException or WorkerOperationException)
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
        if (_interaction.PickDumpFile() is { } path)
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
        catch (Exception ex) when (ex is WorkerStartException or WorkerCrashedException or WorkerOperationException)
        {
            Status = ex.Message;
        }

        Refresh();
    }

    partial void OnSelectedChanged(SnapshotItemViewModel? value)
    {
        Navigator.Reset();
        Types.Show(value?.Handle);
    }

    /// <summary>
    /// Opens a compare row's type on the Snapshot tab: its objects in the current snapshot, or in the baseline for a type
    /// the current snapshot no longer has (ADR-018; sprint 1.5 review).
    /// </summary>
    [RelayCommand]
    private void OpenCompareRow(DiffRowItem? row)
    {
        if (row is not { IsLoaded: true } || Compare.Current is not { } current)
        {
            return;
        }

        if (row.TypeName is null)
        {
            Compare.Status = "Group by type to open a row's objects.";
            return;
        }

        var target = row.CurrentObjects == 0 && Compare.Baseline is { } baseline ? baseline : current;
        Selected = target;
        SelectedTab = 0;
        Navigator.OpenInstances(target.Handle, row.TypeName, row.Module);
    }

    private void OnSnapshotsChanged(object? sender, EventArgs e) => _dispatcher.Post(Refresh);

    private void Refresh()
    {
        var handles = _session.Snapshots;
        for (var i = Snapshots.Count - 1; i >= 0; i--)
        {
            if (!handles.Contains(Snapshots[i].Handle))
            {
                Snapshots.RemoveAt(i);
            }
        }

        foreach (var handle in handles)
        {
            if (!Snapshots.Any(s => s.Handle == handle))
            {
                Snapshots.Add(new SnapshotItemViewModel(handle));
            }
        }

        if (Selected is not null && !Snapshots.Contains(Selected))
        {
            Selected = Snapshots.FirstOrDefault();
        }
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
