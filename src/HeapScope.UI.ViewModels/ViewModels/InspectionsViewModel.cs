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
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.UI.ViewModels;

/// <summary>One inspection on the overview: its finding, cause, fix and first rows, or why it cannot run.</summary>
public sealed partial class InspectionTileViewModel(InspectionInfo info) : ObservableObject
{
    public InspectionInfo Info { get; } = info ?? throw new ArgumentNullException(nameof(info));

    public string Title => Info.Title;

    public string Cause => Info.Cause;

    public string Fix => Info.Fix;

    public bool IsAvailable => Info.Unavailable is null;

    /// <summary>"Waiting", "Running...", an error, or null when the finding is shown.</summary>
    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _summary;

    [ObservableProperty]
    private string? _note;

    /// <summary>The first rows, one line each, for the tile.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _firstRows = [];

    /// <summary>Whether the inspection found anything (tiles with findings stand out).</summary>
    [ObservableProperty]
    private bool _hasFindings;

    public InspectionRun? Run { get; internal set; }
}

/// <summary>A heap kind in the fragmentation bars: live and free bytes, and what is pinned there.</summary>
public sealed record FragmentItem(string Heap, long Live, long? Free, long PinnedObjects, long PinnedBytes)
{
    /// <summary>The free share of the heap (0 when unknown), for the bar's width.</summary>
    public double FreeFraction => Free is { } free && free + Live > 0 ? (double)free / (free + Live) : 0;

    public string Text => string.Create(CultureInfo.CurrentCulture,
        $"{Heap}: {Format.Bytes(Live)} live, {(Free is { } free ? Format.Bytes(free) + $" free ({FreeFraction:P0})" : "free unknown")}{(PinnedObjects > 0 ? $", {PinnedObjects:N0} pinned ({Format.Bytes(PinnedBytes)})" : string.Empty)}");
}

/// <summary>What an inspection row opens: the objects of a type in a scope, or one object.</summary>
public sealed record InspectionTarget(string? TypeName, string? Module, InstanceScope Scope, ulong Address);

/// <summary>
/// The Inspections tab (sprint 1.6, dotMemory's inspections overview): a tile per inspection with its finding, cause and
/// fix, fragmentation bars, and the selected inspection's rows. The inspections of a snapshot run once, one at a time,
/// the first time the tab is shown for it; double-clicking a row opens its objects on the Snapshot tab.
/// </summary>
public sealed partial class InspectionsViewModel : ObservableObject
{
    private const int TileRows = 3;

    private readonly SnapshotSession _session;
    private readonly ILogger _logger;
    private CancellationTokenSource? _running;
    private bool _started;

    [ObservableProperty]
    private InspectionTileViewModel? _selected;

    [ObservableProperty]
    private VirtualRowList<CellRowItem> _rows = Empty();

    /// <summary>The selected inspection's visible columns, with their index in the rows' cells.</summary>
    [ObservableProperty]
    private IReadOnlyList<(ResultColumn Column, int Index)> _columns = [];

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isActive;

    public InspectionsViewModel(SnapshotSession session, ILogger? logger = null, INavigator? navigator = null)
    {
        _navigator = navigator;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger ?? NullLogger.Instance;
    }

    public SnapshotHandle? Snapshot { get; private set; }

    public ObservableCollection<InspectionTileViewModel> Tiles { get; } = [];

    public ObservableCollection<FragmentItem> Fragments { get; } = [];

    public bool HasFragments => Fragments.Count > 0;

    /// <summary>The run in progress (tests and smoke runs await it).</summary>
    public Task RunTask { get; private set; } = Task.CompletedTask;

    // Where a row leads: the Snapshots page, which picks the tab (2.3.7 review M8).
    private readonly INavigator? _navigator;

    /// <summary>Shows <paramref name="snapshot"/>'s inspections (null clears); they run when the tab is active.</summary>
    public void Show(SnapshotHandle? snapshot)
    {
        _running?.Cancel();
        _running?.Dispose();
        _running = null;
        foreach (var tile in Tiles)
        {
            Release(tile.Run);
        }

        Tiles.Clear();
        Fragments.Clear();
        OnPropertyChanged(nameof(HasFragments));
        Selected = null;
        Status = null;
        Snapshot = snapshot;
        _started = false;
        if (IsActive)
        {
            Start();
        }
    }

    partial void OnIsActiveChanged(bool value)
    {
        if (value && !_started)
        {
            Start();
        }
    }

    partial void OnSelectedChanged(InspectionTileViewModel? value)
    {
        Rows.Cancel();
        if (value?.Run is { } run)
        {
            var columns = run.Rows.Info.Columns;
            Columns = [.. columns.Select((c, i) => (c, i)).Where(c => !InspectionColumns.IsHidden(c.c.Name))];
            Rows = CellRowItem.CreateList(run.Rows, run.Rows.Info, ex => Status = ex.Message, _logger);
        }
        else
        {
            Columns = [];
            Rows = Empty();
        }
    }

    [RelayCommand]
    private void OpenRow(CellRowItem? row)
    {
        if (row is not { IsLoaded: true } || Snapshot is not { } snapshot || TargetOf(row) is not { } target)
        {
            return;
        }

        // The objects of its type in its scope, or its object, on the Snapshot tab.
        _navigator?.Navigate(target.TypeName is { } typeName
            ? NavigationTarget.Instances(snapshot, typeName, target.Module, target.Scope, target.Address)
            : NavigationTarget.Object(snapshot, target.Address));
    }

    /// <summary>A row's object (when it names one), or its type's group, in the dominator tree (sprint 2.4).</summary>
    [RelayCommand]
    private void ShowInRetainers(CellRowItem? row)
    {
        if (row is { IsLoaded: true } && Snapshot is { } snapshot && TargetOf(row) is { } target)
        {
            _navigator?.Navigate(target.Address != 0
                ? NavigationTarget.Retainers(snapshot, address: target.Address)
                : NavigationTarget.Retainers(snapshot, typeName: target.TypeName, module: target.Module));
        }
    }

    /// <summary>
    /// What a row's objects retain together (sprint 2.4). Only for a row that stands for every object of its type: a
    /// scoped row (some of a type's objects, such as the empty collections) would get the whole type's answer, so it is
    /// off until sets narrower than a type exist (the 2.4 review; Backlog).
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsWholeType))]
    private void RetainedBySet(CellRowItem? row)
    {
        if (IsWholeType(row) && Snapshot is { } snapshot && TargetOf(row!) is { TypeName: { } typeName } target)
        {
            _navigator?.Navigate(NavigationTarget.RetainedBySet(snapshot, typeName, target.Module));
        }
    }

    private static bool IsWholeType(CellRowItem? row) =>
        row is { IsLoaded: true } && TargetOf(row) is { TypeName: not null, Scope: InstanceScope.All, Address: 0 };

    /// <summary>What a row opens: its hidden type, module and scope, or its address alone (ADR-020).</summary>
    public static InspectionTarget? TargetOf(CellRowItem row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var address = row[InspectionColumns.Address] is { } text ? Cells.Address(text) : 0;
        if (row[InspectionColumns.TypeName] is { Length: > 0 } typeName && Enum.TryParse<InstanceScope>(row[InspectionColumns.Scope], out var scope))
        {
            return new InspectionTarget(typeName, row[InspectionColumns.Module], scope, address);
        }

        return address != 0 ? new InspectionTarget(null, null, InstanceScope.All, address) : null;
    }

    private void Start()
    {
        if (Snapshot is not { } snapshot)
        {
            return;
        }

        _started = true;
        _running = new CancellationTokenSource();
        RunTask = RunAllAsync(snapshot, _running.Token);
    }

    private async Task RunAllAsync(SnapshotHandle snapshot, CancellationToken cancellationToken)
    {
        try
        {
            Status = "Listing inspections...";
            var inspections = await _session.ListInspectionsAsync(snapshot, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            foreach (var info in inspections)
            {
                Tiles.Add(new InspectionTileViewModel(info) { Status = info.Unavailable is null ? "Waiting" : null, Summary = info.Unavailable });
            }

            Status = null;
            foreach (var tile in Tiles.Where(t => t.IsAvailable).ToList())
            {
                await RunOneAsync(snapshot, tile, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                Selected ??= tile.HasFindings ? tile : null;
            }

            Selected ??= Tiles.FirstOrDefault(t => t.Run is not null);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Another snapshot (or none) is shown now.
        }
        catch (Exception ex) when (ex is WorkerException)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            // The run is not awaited by anyone: its failure must be logged and shown, never lost (review of 1.6).
            _logger.LogError(ex, "Running the inspections failed");
            Status = "The inspections could not be run. Details are in the log; select the snapshot again to retry.";
        }
    }

    private async Task RunOneAsync(SnapshotHandle snapshot, InspectionTileViewModel tile, CancellationToken cancellationToken)
    {
        tile.Status = "Running...";
        try
        {
            var run = await _session.RunInspectionAsync(snapshot, tile.Info.Name, null, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                Release(run);
                return;
            }

            tile.Run = run;
            var result = run.Result;
            tile.Summary = string.Create(CultureInfo.CurrentCulture,
                $"{result.Count:N0} found, {Format.Bytes(result.Bytes)}{(result.WastedBytes is { } wasted ? $", {Format.Bytes(wasted)} wasted" : string.Empty)}");
            tile.Note = result.Note;
            tile.HasFindings = result.Count > 0;
            var isFragmentation = tile.Info.Name == InspectionNames.Fragmentation;
            var first = await run.Rows.GetRowsAsync(0, isFragmentation ? run.Rows.Info.TotalCount : TileRows, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                // Another snapshot is shown now: nothing of this one may reach the shared bars or the grid.
                return;
            }

            var key = run.Rows.Info.Columns.Select((c, i) => (c, i)).First(c => !InspectionColumns.IsHidden(c.c.Name)).i;
            tile.FirstRows = [.. first.Rows.Take(TileRows).Select(r => r[key] ?? string.Empty)];
            if (isFragmentation)
            {
                ShowFragments(run.Rows.Info.Columns, first.Rows);
            }

            tile.Status = null;

            // A tile selected while it was still running shows its rows now.
            if (ReferenceEquals(Selected, tile))
            {
                OnSelectedChanged(tile);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is WorkerOperationException or WorkerCrashedException)
        {
            _logger.LogWarning(ex, "The {Inspection} inspection failed", tile.Info.Name);
            tile.Status = ex.Message;
        }
    }

    private void ShowFragments(IReadOnlyList<ResultColumn> columns, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        int At(string name) => columns.Select((c, i) => (c, i)).Single(c => c.c.Name == name).i;
        var (heap, live, free, pinned, pinnedBytes) = (At(InspectionColumns.Heap), At(InspectionColumns.Live), At(InspectionColumns.Free), At(InspectionColumns.PinnedObjects), At(InspectionColumns.PinnedBytes));
        Fragments.Clear();
        foreach (var row in rows)
        {
            Fragments.Add(new FragmentItem(
                row[heap] ?? "?",
                Cells.Long(row, live),
                row[free] is null ? null : Cells.Long(row, free),
                Cells.Long(row, pinned),
                Cells.Long(row, pinnedBytes)));
        }

        OnPropertyChanged(nameof(HasFragments));
    }

    private void Release(InspectionRun? run) => run?.Rows.ReleaseInBackground(_logger);

    private static VirtualRowList<CellRowItem> Empty() =>
        CellRowItem.CreateList(null, new ResultSetInfo(Guid.Empty, 0, []));
}
