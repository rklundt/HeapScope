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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;

/// <summary>
/// Where each value of a type summary row is, found from the result set's column names, so a change in the worker's
/// column order cannot put values under the wrong header. Name, Objects and Bytes are required.
/// </summary>
public sealed class TypeRowLayout
{
    private TypeRowLayout(IReadOnlyList<ResultColumn> columns)
    {
        Name = ResultColumns.Require(columns, "Name");
        Objects = ResultColumns.Require(columns, "Objects");
        Bytes = ResultColumns.Require(columns, "Bytes");
        Percent = ResultColumns.Find(columns, "Percent");
        Gen0 = ResultColumns.Find(columns, "Gen0");
        Gen1 = ResultColumns.Find(columns, "Gen1");
        Gen2 = ResultColumns.Find(columns, "Gen2");
        LargeObjectHeap = ResultColumns.Find(columns, "LargeObjectHeap");
        PinnedObjectHeap = ResultColumns.Find(columns, "PinnedObjectHeap");
        Frozen = ResultColumns.Find(columns, "Frozen");
        Retained = ResultColumns.Find(columns, TypeSummaryColumns.Retained);
        TypeName = ResultColumns.Find(columns, TypeSummaryColumns.TypeName);
        Module = ResultColumns.Find(columns, TypeSummaryColumns.Module);
    }

    public int Name { get; }

    public int Objects { get; }

    public int Bytes { get; }

    public int Percent { get; }

    public int Gen0 { get; }

    public int Gen1 { get; }

    public int Gen2 { get; }

    public int LargeObjectHeap { get; }

    public int PinnedObjectHeap { get; }

    public int Frozen { get; }

    /// <summary>The retained bytes column (sprint 2.3); -1 when not asked for.</summary>
    public int Retained { get; }

    public int TypeName { get; }

    public int Module { get; }

    public static TypeRowLayout From(IReadOnlyList<ResultColumn> columns) => new(columns ?? throw new ArgumentNullException(nameof(columns)));
}

/// <summary>
/// One row of the types grid, filled when its page arrives (data virtualization). Values are set together and announced
/// with one "all properties changed" notification: a page fills 200 rows, and a notification per value made the grid's
/// rebinding exceed the 100 ms dispatcher budget (sprint 1.3 smoke run).
/// </summary>
public sealed class TypeRowItem(int index) : ObservableObject, IIndexedRow
{
    public int Index { get; } = index;

    public string Name { get; private set; } = string.Empty;

    public long Objects { get; private set; }

    public long Bytes { get; private set; }

    public double Percent { get; private set; }

    public long Gen0 { get; private set; }

    public long Gen1 { get; private set; }

    public long Gen2 { get; private set; }

    public long LargeObjectHeap { get; private set; }

    public long PinnedObjectHeap { get; private set; }

    public long Frozen { get; private set; }

    /// <summary>The row's retained bytes, a lower bound (sprint 2.3); 0 when not asked for.</summary>
    public long Retained { get; private set; }

    public bool IsLoaded { get; private set; }

    /// <summary>The raw type name that opens the row's instances; null for namespace and assembly rows.</summary>
    public string? TypeName { get; private set; }

    /// <summary>The module file name of <see cref="TypeName"/> (empty when none).</summary>
    public string? Module { get; private set; }

    /// <summary>A row list over a type summary result set; fails at once when a required column is missing.</summary>
    public static VirtualRowList<TypeRowItem> CreateList(ResultSetHandle? rows, ResultSetInfo info, Action<Exception>? onError = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        var layout = TypeRowLayout.From(info.Columns);
        return new VirtualRowList<TypeRowItem>(rows, info.TotalCount, i => new TypeRowItem(i), (row, cells) => row.Fill(cells, layout), onError, logger);
    }

    internal void Fill(IReadOnlyList<string?> cells, TypeRowLayout layout)
    {
        Name = Cells.Text(cells, layout.Name);
        Objects = Cells.Long(cells, layout.Objects);
        Bytes = Cells.Long(cells, layout.Bytes);
        Percent = Cells.Double(cells, layout.Percent);
        Gen0 = Cells.Long(cells, layout.Gen0);
        Gen1 = Cells.Long(cells, layout.Gen1);
        Gen2 = Cells.Long(cells, layout.Gen2);
        LargeObjectHeap = Cells.Long(cells, layout.LargeObjectHeap);
        PinnedObjectHeap = Cells.Long(cells, layout.PinnedObjectHeap);
        Frozen = Cells.Long(cells, layout.Frozen);
        Retained = Cells.Long(cells, layout.Retained);
        TypeName = layout.TypeName >= 0 ? Cells.Text(cells, layout.TypeName) : null;
        Module = layout.Module >= 0 ? Cells.Text(cells, layout.Module) : null;
        IsLoaded = true;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Invariant-culture cell parsing; a missing column (index -1) or empty cell reads as zero or empty.</summary>
internal static class Cells
{
    public static string Text(IReadOnlyList<string?> cells, int index) => index >= 0 && index < cells.Count ? cells[index] ?? string.Empty : string.Empty;

    public static long Long(IReadOnlyList<string?> cells, int index) =>
        index >= 0 && index < cells.Count && long.TryParse(cells[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>An address cell (<c>0x1f2a</c>) as a number; 0 when it is not one.</summary>
    public static ulong Address(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : 0;

    public static double Double(IReadOnlyList<string?> cells, int index) =>
        index >= 0 && index < cells.Count && double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

/// <summary>A heap kind in the overview bar: share of the snapshot's bytes, in its fixed color.</summary>
public sealed record HeapKindShare(HeapKindDto Kind, string Label, long Objects, long Bytes, double Fraction)
{
    public string BrushKey => "HeapKind." + Kind;

    public string Text => string.Create(CultureInfo.CurrentCulture, $"{Label} {Format.Bytes(Bytes)} ({Fraction * 100:N1}%)");
}

/// <summary>
/// The types grid of a snapshot (sprint 1.3, dotMemory's types view): grouping, type-to-filter (debounced) and sorting
/// re-query the worker; rows are virtualized; generation columns show only when the snapshot has generations.
/// </summary>
public sealed partial class TypeSummaryViewModel : TypeGridViewModel<TypeRowItem>, INavigationSubject
{
    private readonly SnapshotSession _session;
    private SnapshotHandle? _snapshot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenerationColumnsShown))]
    [NotifyPropertyChangedFor(nameof(VisibleColumnGroups))]
    private bool _hasGenerations = true;

    /// <summary>
    /// Whether the user asked for bytes per generation: off by default (dotMemory research 7, a generations sub-view),
    /// and the six columns were half of the grid's layout time (sprint 1.5 smoke runs).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenerationColumnsShown))]
    [NotifyPropertyChangedFor(nameof(VisibleColumnGroups))]
    private bool _showGenerations;

    /// <summary>Whether the per-generation columns are shown: asked for, and the snapshot has generations.</summary>
    public bool GenerationColumnsShown => HasGenerations && ShowGenerations;

    [ObservableProperty]
    private string? _generationsExplanation;

    [ObservableProperty]
    private IReadOnlyList<HeapKindShare> _heapKinds = [];

    /// <summary>
    /// Whether the user asked for retained bytes (sprint 2.3): off by default, since the first such query of a snapshot
    /// computes its dominators (seconds on a large dump; decision 5).
    /// </summary>
    [ObservableProperty]
    private bool _showRetained;

    /// <summary>Whether the shown rows carry retained bytes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleColumnGroups))]
    private bool _retainedColumnShown;

    /// <summary>The optional column groups shown (2.3.7 review M13).</summary>
    public IReadOnlyList<string> VisibleColumnGroups => ColumnGroups.Of((ColumnGroups.Retained, RetainedColumnShown), (ColumnGroups.Generations, GenerationColumnsShown));

    /// <summary>How the retained sizes were obtained and what a row's value means, or why they are not shown; null without them.</summary>
    [ObservableProperty]
    private string? _retainedNote;

    private bool _retainedOffQuietly;

    public TypeSummaryViewModel(SnapshotSession session, IUiDispatcher dispatcher, ILogger<TypeSummaryViewModel>? logger = null, INavigator? navigator = null)
        : this(session, dispatcher, null, null, logger, navigator)
    {
    }

    /// <summary>The Types view of an object set (sprint 3.1): the members only; a row opens the set's members of its type.</summary>
    public TypeSummaryViewModel(SnapshotSession session, IUiDispatcher dispatcher, ObjectSetQuery? set, string? setTitle, ILogger<TypeSummaryViewModel>? logger = null, INavigator? navigator = null)
        : base(TypeSummaryColumns.Bytes, dispatcher, logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Set = set;
        SetTitle = setTitle;
        OpenRowCommand = new RelayCommand<TypeRowItem?>(row => OpenRow(row, navigator));
        ShowInRetainersCommand = new RelayCommand<TypeRowItem?>(row => Link(row, navigator, (s, r) => NavigationTarget.Retainers(s, typeName: r.TypeName, module: r.Module)));
        RetainedBySetCommand = new RelayCommand<TypeRowItem?>(row => Link(row, navigator, (s, r) => NavigationTarget.RetainedBySet(s, r.TypeName!, r.Module)));
        OpenAsSetCommand = new RelayCommand<System.Collections.IList?>(rows => OpenAsSet(rows, navigator), _ => Set is null);
    }

    /// <summary>The set whose members are summarized (sprint 3.1); null for the whole snapshot.</summary>
    public ObjectSetQuery? Set { get; }

    /// <summary>The set's title, with <see cref="Set"/>.</summary>
    public string? SetTitle { get; }

    /// <summary>Whether this grid summarizes a set: the whole-type links and retained sizes are off.</summary>
    public bool IsSet => Set is not null;

    /// <summary>
    /// Opens the selected rows as one object set (sprint 3.1): several type rows, or one namespace or assembly row. The
    /// parameter is the grid's selected rows.
    /// </summary>
    public System.Windows.Input.ICommand OpenAsSetCommand { get; }

    /// <summary>The set the selected rows make, and its title; null (with <see cref="GridQueryViewModel{TRow}.Status"/> saying why) when they make none.</summary>
    public (ObjectSetSpec Spec, string Title)? SetOf(IEnumerable<TypeRowItem> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var selected = rows.ToList();
        var loaded = selected.Where(r => r.IsLoaded).ToList();
        if (loaded.Count == 0)
        {
            return null;
        }

        // A set never quietly leaves rows out (the 3.1 review): rows not loaded yet, or more types than a set takes.
        if (loaded.Count < selected.Count)
        {
            Status = string.Create(CultureInfo.CurrentCulture, $"{selected.Count - loaded.Count:N0} of the selected rows are not loaded yet. Scroll through them, or filter the grid, and open the set again.");
            return null;
        }

        if (loaded.Count > ObjectSetLimits.MaxTypes)
        {
            Status = string.Create(CultureInfo.CurrentCulture, $"A set takes at most {ObjectSetLimits.MaxTypes:N0} types; {loaded.Count:N0} are selected. Group by namespace or assembly to open a larger group.");
            return null;
        }

        if (GroupBy == TypeGrouping.Type)
        {
            var keys = loaded.Where(r => r.TypeName is not null).Select(r => new TypeKeyDto(r.TypeName!, r.Module)).ToList();
            return (ObjectSetSpec.OfTypes(keys), keys.Count == 1 ? loaded[0].Name : string.Create(CultureInfo.CurrentCulture, $"{keys.Count:N0} types"));
        }

        if (loaded.Count > 1)
        {
            Status = GroupBy == TypeGrouping.Namespace ? "Open one namespace at a time as a set, or group by type and select its types." : "Open one assembly at a time as a set, or group by type and select its types.";
            return null;
        }

        return GroupBy == TypeGrouping.Namespace
            ? (ObjectSetSpec.OfNamespace(loaded[0].Name), "Namespace " + loaded[0].Name)
            : (ObjectSetSpec.OfAssembly(loaded[0].Name), "Assembly " + loaded[0].Name);
    }

    private void OpenAsSet(System.Collections.IList? rows, INavigator? navigator)
    {
        if (_snapshot is { } snapshot && Set is null && rows is not null && SetOf(rows.OfType<TypeRowItem>()) is { } set)
        {
            navigator?.Navigate(NavigationTarget.ObjectSet(snapshot, new ObjectSetQuery(set.Spec), set.Title));
        }
    }

    /// <summary>A type row's group in the dominator tree, on the Retainers tab (sprint 2.4).</summary>
    public System.Windows.Input.ICommand ShowInRetainersCommand { get; }

    /// <summary>What a type row's objects retain together, on the Retainers tab (sprint 2.4).</summary>
    public System.Windows.Input.ICommand RetainedBySetCommand { get; }

    /// <summary>The snapshot shown, if any.</summary>
    public SnapshotHandle? Snapshot => _snapshot;

    /// <summary>The navigation title of the root subject (a set's Types view has its set's title).</summary>
    public string Title => SetTitle ?? "Types";

    private void Link(TypeRowItem? row, INavigator? navigator, Func<SnapshotHandle, TypeRowItem, NavigationTarget> target)
    {
        if (Set is not null)
        {
            // These follow all of a type's objects, not the set's members (sprint 3.1).
            return;
        }

        if (row is { IsLoaded: true } && _snapshot is { } snapshot)
        {
            if (row.TypeName is null)
            {
                Status = "Group by type to follow a row's objects.";
                return;
            }

            navigator?.Navigate(target(snapshot, row));
        }
    }

    // A type row opens its objects (sprint 1.5); grouped rows have no single type.
    private void OpenRow(TypeRowItem? row, INavigator? navigator)
    {
        if (row is not { IsLoaded: true } || _snapshot is not { } snapshot)
        {
            return;
        }

        if (row.TypeName is null)
        {
            // A namespace or assembly row opens as a set of its types (sprint 3.1); within a set, group by type.
            if (Set is null && SetOf([row]) is { } group)
            {
                navigator?.Navigate(NavigationTarget.ObjectSet(snapshot, new ObjectSetQuery(group.Spec), group.Title));
                return;
            }

            Status = "Group by type to open a row's objects.";
            return;
        }

        navigator?.Navigate(Set is { } set
            ? NavigationTarget.SetInstances(snapshot, set, SetTitle!, row.TypeName, row.Module)
            : NavigationTarget.Instances(snapshot, row.TypeName, row.Module));
    }

    protected override bool CanQuery => _snapshot is not null;

    protected override string BusyText => "Summarizing...";

    /// <summary>Shows a snapshot (or nothing) and queries it with the current grouping, filter and sort.</summary>
    public void Show(SnapshotHandle? snapshot)
    {
        _snapshot = snapshot;
        if (snapshot is null)
        {
            ClearResults();
            return;
        }

        Requery();
    }

    protected override void OnCleared() => HeapKinds = [];

    partial void OnShowRetainedChanged(bool value)
    {
        if (_retainedOffQuietly)
        {
            return;
        }

        if (!value && SortColumn == TypeSummaryColumns.Retained)
        {
            SortColumn = TypeSummaryColumns.Bytes;
            Descending = true;
        }

        if (_snapshot is not null)
        {
            Requery();
        }
    }

    protected override async Task<GridQuery<TypeRowItem>> RunQueryAsync(CancellationToken cancellationToken)
    {
        var snapshot = _snapshot!;
        var groupBy = GroupBy;
        var retained = ShowRetained && Set is null;

        // The first retained query computes the dominators: its phases replace the busy text until it answers.
        var finished = false;
        var progress = retained ? new Progress<JobProgress>(p => Status = finished || cancellationToken.IsCancellationRequested ? Status : ReferrersText.Progress(p)) : null;
        string? retainedFailure = null;
        TypeSummary summary;
        try
        {
            summary = await _session.QueryTypeSummaryAsync(snapshot, groupBy, Filter, SortColumn, Descending, retained, progress, cancellationToken, Set);
        }
        catch (WorkerOperationException ex) when (retained && !cancellationToken.IsCancellationRequested)
        {
            // Review M8: retained sizes are optional, so the rows load without them and the note says why.
            retainedFailure = ex.Message;
            var bytes = SortColumn == TypeSummaryColumns.Retained;
            summary = await _session.QueryTypeSummaryAsync(snapshot, groupBy, Filter, bytes ? TypeSummaryColumns.Bytes : SortColumn, bytes || Descending, false, null, cancellationToken, Set);
        }

        finished = true;
        var result = summary.Result;
        return new GridQuery<TypeRowItem>(
            summary.Rows,
            () =>
            {
                HasGenerations = result.HasGenerations;
                GenerationsExplanation = result.HasGenerations
                    ? null
                    : CapabilityDescriptions.ExplainMissing(SnapshotFeatures.Generations, snapshot.Descriptor.Kind);
                RetainedColumnShown = result.Dominators is not null;
                // Whether the rows are lower bounds comes with the column (sprint 2.4, 2.3.7 review M7), not from here.
                var retainedBound = result.Rows.Columns.FirstOrDefault(c => c.Name == TypeSummaryColumns.Retained)?.Bound;
                RetainedNote = result.Dominators is { } dominators
                    ? RetainedText.Source(dominators) + (retainedBound == ValueBound.LowerBound ? " " + ValueBoundTexts.LowerBound : string.Empty)
                    : null;
                if (retainedFailure is not null)
                {
                    TurnRetainedOffQuietly();
                    RetainedNote = RetainedText.NotShown(retainedFailure);
                }

                HeapKinds = [.. result.HeapKinds.Select(k => new HeapKindShare(k.Kind, Label(k.Kind), k.Objects, k.Bytes, result.TotalBytes == 0 ? 0 : (double)k.Bytes / result.TotalBytes))];
                Totals = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{result.Rows.TotalCount:N0} of {result.UnfilteredRows:N0} {Plural(groupBy)}; {result.TotalObjects:N0} objects, {Format.Bytes(result.TotalBytes)} ({(Set is null ? Scope(snapshot.Descriptor.Kind) : "the set's objects")})");
            },
            onError => TypeRowItem.CreateList(summary.Rows, result.Rows, onError, Logger));
    }

    /// <summary>Unchecks Retained sizes after a failed retained query, without querying again, and sorts by bytes.</summary>
    private void TurnRetainedOffQuietly()
    {
        _retainedOffQuietly = true;
        ShowRetained = false;
        _retainedOffQuietly = false;
        if (SortColumn == TypeSummaryColumns.Retained)
        {
            SortColumn = TypeSummaryColumns.Bytes;
            Descending = true;
        }
    }

    // A gcdump's capture ran a full collection first, so it holds survivors only (sprint 1.8).
    private static string Scope(SnapshotKindDto kind) =>
        kind == SnapshotKindDto.GcDump ? "the objects that survived the gcdump's collection" : "all objects in the dump, unreachable ones included";

    private static string Label(HeapKindDto kind) => kind switch
    {
        HeapKindDto.LargeObjectHeap => "LOH",
        HeapKindDto.PinnedObjectHeap => "POH",
        HeapKindDto.Unknown => "Unknown generation",
        _ => kind.ToString(),
    };
}
