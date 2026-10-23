using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.UI.Wpf.Services;

namespace HeapScope.UI.Wpf.ViewModels;

/// <summary>One row of the top types table. Numbers stay numeric so the grid sorts them correctly.</summary>
public sealed record TypeRow(string Type, long Count, long Bytes);

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
/// The Snapshots page (sprint 1.2): open a dump, see the open snapshots, and an overview of the selected one with its
/// largest types (dotMemory's "Largest Size" tile, research section 7). The full types grid is sprint 1.3.
/// </summary>
public sealed partial class SnapshotsViewModel : ObservableObject, IDisposable
{
    /// <summary>Rows shown in the overview's largest types table.</summary>
    public const int TopTypeCount = 200;

    private readonly SnapshotSession _session;
    private readonly WorkerOperations _operations;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUserInteraction _interaction;
    private int _overviewVersion;

    [ObservableProperty]
    private SnapshotItemViewModel? _selected;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isOpening;

    public SnapshotsViewModel(
        SnapshotSession session,
        WorkerOperations operations,
        IUiDispatcher dispatcher,
        IUserInteraction interaction)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _session.SnapshotsChanged += OnSnapshotsChanged;
    }

    public ObservableCollection<SnapshotItemViewModel> Snapshots { get; } = [];

    public ObservableCollection<TypeRow> TopTypes { get; } = [];

    /// <summary>The overview load in progress, if any (tests and smoke runs await it).</summary>
    public Task OverviewLoad { get; private set; } = Task.CompletedTask;

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
        TopTypes.Clear();
        if (value is not null)
        {
            OverviewLoad = LoadOverviewAsync(value, ++_overviewVersion);
        }
    }

    private async Task LoadOverviewAsync(SnapshotItemViewModel item, int version)
    {
        try
        {
            var summary = await _session.GetTypeSummaryAsync(item.Handle, CancellationToken.None);
            try
            {
                var page = await _operations.GetRowsAsync(summary.Id, 0, TopTypeCount, CancellationToken.None);
                if (version != _overviewVersion)
                {
                    return;
                }

                foreach (var row in page.Rows)
                {
                    TopTypes.Add(new TypeRow(
                        row[0] ?? string.Empty,
                        long.Parse(row[1]!, CultureInfo.InvariantCulture),
                        long.Parse(row[2]!, CultureInfo.InvariantCulture)));
                }
            }
            finally
            {
                await _operations.ReleaseResultSetAsync(summary.Id, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WorkerStartException or WorkerCrashedException or WorkerOperationException or OperationCanceledException)
        {
            Status = ex.Message;
        }
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
}
