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

    /// <summary>A leak detector (sprint 2.6), shown in its own group above the waste inspections.</summary>
    public bool IsDetector => Info.Kind == InspectionKinds.Leak;

    /// <summary>The group's heading on the first tile of each group ("Leak detectors", "Memory waste"); null on the others.</summary>
    public string? GroupHeader { get; internal set; }

    public bool HasGroupHeader => GroupHeader is not null;

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

    /// <summary>The baseline <see cref="Run"/> used (sprint 2.6 review H6): a tile run with another one is run again.</summary>
    internal SnapshotHandle? RanWith { get; set; }
}

/// <summary>A heap kind in the fragmentation bars: live and free bytes, and what is pinned there.</summary>
public sealed record FragmentItem(string Heap, long Live, long? Free, long PinnedObjects, long PinnedBytes)
{
    /// <summary>The free share of the heap (0 when unknown), for the bar's width.</summary>
    public double FreeFraction => Free is { } free && free + Live > 0 ? (double)free / (free + Live) : 0;

    public string Text => string.Create(CultureInfo.CurrentCulture,
        $"{Heap}: {Format.Bytes(Live)} live, {(Free is { } free ? Format.Bytes(free) + $" free ({FreeFraction:P0})" : "free unknown")}{(PinnedObjects > 0 ? $", {PinnedObjects:N0} pinned ({Format.Bytes(PinnedBytes)})" : string.Empty)}");
}

/// <summary>A choice of baseline for the detectors that measure growth (sprint 2.6): another open snapshot, or none.</summary>
public sealed record BaselineChoice(string Text, SnapshotHandle? Snapshot)
{
    public override string ToString() => Text;
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
    private CancellationTokenSource? _rerun;
    private Task _runAll = Task.CompletedTask;
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

    /// <summary>The baseline the growth detectors compare with (sprint 2.6); "None" by default unless an earlier snapshot of the same process is open.</summary>
    [ObservableProperty]
    private BaselineChoice? _baseline;

    private bool _choosingBaseline;

    public InspectionsViewModel(SnapshotSession session, ILogger? logger = null, INavigator? navigator = null)
    {
        _navigator = navigator;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger ?? NullLogger.Instance;
    }

    public SnapshotHandle? Snapshot { get; private set; }

    /// <summary>The tiles: the leak detectors first, then the waste inspections.</summary>
    public ObservableCollection<InspectionTileViewModel> Tiles { get; } = [];

    /// <summary>"None" and the other open snapshots, for the baseline picker.</summary>
    public ObservableCollection<BaselineChoice> BaselineChoices { get; } = [];

    /// <summary>Whether any inspection uses a baseline (the picker is shown).</summary>
    public bool UsesBaseline => Tiles.Any(t => t.Info.UsesBaseline && t.IsAvailable);

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
        _rerun?.Dispose();
        _rerun = null;
        foreach (var tile in Tiles)
        {
            Release(tile.Run);
        }

        Tiles.Clear();
        Fragments.Clear();
        OnPropertyChanged(nameof(HasFragments));
        OnPropertyChanged(nameof(UsesBaseline));
        Selected = null;
        Status = null;
        Snapshot = snapshot;
        _started = false;
        ChooseBaselines(snapshot);
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
        RunTask = _runAll = RunAllAsync(snapshot, _running.Token);
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

            // Detectors first, each group under its heading (sprint 2.6).
            string? group = null;
            foreach (var info in inspections.OrderBy(i => i.Kind == InspectionKinds.Leak ? 0 : 1))
            {
                var heading = info.Kind == InspectionKinds.Leak ? "Leak detectors" : "Memory waste";
                Tiles.Add(new InspectionTileViewModel(info)
                {
                    Status = info.Unavailable is null ? "Waiting" : null,
                    Summary = info.Unavailable,
                    GroupHeader = heading == group ? null : heading,
                });
                group = heading;
            }

            OnPropertyChanged(nameof(UsesBaseline));

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

            // A baseline chosen while the tiles ran (review H6): the growth tiles run with an older one run again, until
            // no other was chosen meanwhile.
            BaselineChoice? chosen;
            do
            {
                chosen = Baseline;
                await RerunAsync(snapshot, StaleTiles(), cancellationToken);
            }
            while (!cancellationToken.IsCancellationRequested && !ReferenceEquals(chosen, Baseline));
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
            var baseline = tile.Info.UsesBaseline ? Baseline?.Snapshot : null;
            _logger.LogInformation(
                "Inspections: running {Inspection} on {Snapshot}{Baseline}",
                tile.Info.Name,
                snapshot.Path,
                baseline is null ? string.Empty : " with the baseline " + baseline.Path);
            var run = await _session.RunInspectionAsync(snapshot, tile.Info.Name, null, cancellationToken, baseline);
            if (cancellationToken.IsCancellationRequested)
            {
                Release(run);
                return;
            }

            tile.Run = run;
            tile.RanWith = baseline;
            var result = run.Result;
            tile.Summary = string.Create(CultureInfo.CurrentCulture,
                $"{result.Count:N0} found, {Format.Bytes(result.Bytes)}{(result.WastedBytes is { } wasted ? $", {Format.Bytes(wasted)} wasted" : string.Empty)}{(baseline is not null && result.BaselineSnapshotId is not null ? $"; growth since {Path.GetFileName(baseline.Path)}" : string.Empty)}");
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
            // Not left "Running..." (review H6): a rerun or another snapshot replaces it.
            tile.Status = tile.Run is null ? "Waiting" : null;
            throw;
        }
        catch (Exception ex) when (ex is WorkerOperationException or WorkerCrashedException)
        {
            _logger.LogWarning(ex, "The {Inspection} inspection failed", tile.Info.Name);
            tile.Status = ex.Message;
        }
    }

    /// <summary>
    /// The baseline choices for <paramref name="snapshot"/>: none, and the other open snapshots, latest first. The latest
    /// one of the same process captured earlier is chosen (a growth detector needs the earlier dump; ADR-018).
    /// </summary>
    private void ChooseBaselines(SnapshotHandle? snapshot)
    {
        _choosingBaseline = true;
        try
        {
            BaselineChoices.Clear();
            BaselineChoices.Add(new BaselineChoice("None", null));
            if (snapshot is null)
            {
                Baseline = BaselineChoices[0];
                return;
            }

            var others = _session.Snapshots.Where(s => s != snapshot && !s.IsClosed)
                .OrderByDescending(s => s.Descriptor.CapturedAtUtc ?? DateTimeOffset.MinValue)
                .ToList();
            foreach (var other in others)
            {
                BaselineChoices.Add(new BaselineChoice(
                    string.Create(CultureInfo.CurrentCulture, $"{Path.GetFileName(other.Path)}{(other.Descriptor.CapturedAtUtc is { } at ? $" ({at.ToLocalTime():g})" : string.Empty)}"),
                    other));
            }

            var mine = snapshot.Descriptor;
            Baseline = BaselineChoices.Skip(1).FirstOrDefault(c =>
                    c.Snapshot!.Descriptor.ProcessName == mine.ProcessName
                    && c.Snapshot.Descriptor.CapturedAtUtc is { } then && mine.CapturedAtUtc is { } now && then < now)
                ?? BaselineChoices[0];
        }
        finally
        {
            _choosingBaseline = false;
        }
    }

    partial void OnBaselineChanged(BaselineChoice? value)
    {
        // Choosing another baseline runs the growth detectors again (only those use it).
        if (_choosingBaseline || !_started || Snapshot is not { } snapshot)
        {
            return;
        }

        _logger.LogInformation("Inspections: baseline {Baseline} chosen for {Snapshot}", value?.Snapshot?.Path ?? "none", snapshot.Path);

        // While the first run goes on, it reruns the stale growth tiles when it ends (review H6): it is not cancelled.
        if (!_runAll.IsCompleted || _running is not { } running)
        {
            return;
        }

        // A rerun has its own token, linked to the snapshot's: another baseline cancels only the rerun.
        _rerun?.Cancel();
        _rerun?.Dispose();
        _rerun = CancellationTokenSource.CreateLinkedTokenSource(running.Token);
        RunTask = RerunAsync(snapshot, StaleTiles(), _rerun.Token);
    }

    /// <summary>The growth tiles whose last run used another baseline than the chosen one.</summary>
    private List<InspectionTileViewModel> StaleTiles() =>
        [.. Tiles.Where(t => t.Info.UsesBaseline && t.IsAvailable && (t.Run is null || !ReferenceEquals(t.RanWith, Baseline?.Snapshot)))];

    private async Task RerunAsync(SnapshotHandle snapshot, List<InspectionTileViewModel> tiles, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var tile in tiles)
            {
                var previous = tile.Run;
                try
                {
                    await RunOneAsync(snapshot, tile, cancellationToken);
                }
                finally
                {
                    // The worker keeps a replaced result set and its lease until released (review M5).
                    if (!ReferenceEquals(previous, tile.Run))
                    {
                        Release(previous);
                    }
                }
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Another baseline or snapshot replaced this run.
        }
        catch (Exception ex) when (ex is WorkerException)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            // Not awaited by anyone (review M5): logged and shown, never lost.
            _logger.LogError(ex, "Running the growth detectors again failed");
            Status = "The growth detectors could not be run again. Details are in the log.";
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
