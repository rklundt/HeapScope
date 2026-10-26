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
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.UI.ViewModels;

/// <summary>A field (or array element) of the inspected object, with nested structs and listed elements as children.</summary>
public sealed partial class FieldNode : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    public FieldNode(ObjectFieldDto field)
    {
        ArgumentNullException.ThrowIfNull(field);
        Name = field.Kind == ObjectFieldKindDto.More ? string.Empty : field.Name;
        TypeName = field.TypeName;
        Kind = field.Kind;
        Value = field.Value;
        Address = field.Address;
        Children = [.. field.Children.Select(c => new FieldNode(c))];

        // Nested structs open by default (they are part of the object); array elements stay collapsed.
        _isExpanded = Kind == ObjectFieldKindDto.Struct;
    }

    public string Name { get; }

    public string TypeName { get; }

    public ObjectFieldKindDto Kind { get; }

    public string Value { get; }

    public ulong Address { get; }

    public IReadOnlyList<FieldNode> Children { get; }

    /// <summary>Whether the value is another object that can be opened (a reference, a string, an array).</summary>
    public bool IsLink => Address != 0 && Kind is ObjectFieldKindDto.Reference or ObjectFieldKindDto.String or ObjectFieldKindDto.Array;
}

/// <summary>One outgoing reference of the inspected object, filled when its page arrives.</summary>
public sealed class ReferenceRowItem(int index) : ObservableObject, IIndexedRow
{
    public int Index { get; } = index;

    public string Field { get; private set; } = string.Empty;

    public ulong Address { get; private set; }

    public string AddressText { get; private set; } = string.Empty;

    public string Type { get; private set; } = string.Empty;

    public long Bytes { get; private set; }

    public bool IsLoaded { get; private set; }

    /// <summary>A row list over a references result set; fails at once when a column is missing.</summary>
    public static VirtualRowList<ReferenceRowItem> CreateList(ResultSetHandle? rows, ResultSetInfo info, Action<Exception>? onError = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        int[] layout =
        [
            ResultColumns.Require(info.Columns, ReferenceColumns.Field),
            ResultColumns.Require(info.Columns, ReferenceColumns.Address),
            ResultColumns.Require(info.Columns, ReferenceColumns.Type),
            ResultColumns.Require(info.Columns, ReferenceColumns.Bytes),
        ];
        return new VirtualRowList<ReferenceRowItem>(rows, info.TotalCount, i => new ReferenceRowItem(i), (row, cells) => row.Fill(cells, layout), onError, logger);
    }

    private void Fill(IReadOnlyList<string?> cells, int[] layout)
    {
        Field = Cells.Text(cells, layout[0]);
        AddressText = Cells.Text(cells, layout[1]);
        Address = Cells.Address(AddressText);
        Type = Cells.Text(cells, layout[2]);
        Bytes = Cells.Long(cells, layout[3]);
        IsLoaded = true;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// One object (sprint 1.5): what the graph knows, its fields as a tree when the snapshot reads its dump (the capability
/// explanation otherwise), its text when it is a string, its outgoing references and (sprint 2.1) the objects referencing
/// it, loaded when the Incoming tab is first shown because the first such query of a snapshot builds its reverse index;
/// references, referrers and field links open further objects through <see cref="OpenCommand"/>.
/// </summary>
public sealed partial class ObjectViewModel : ObservableObject
{
    private readonly SnapshotSession _session;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private ResultSetHandle? _references;
    private ResultSetHandle? _incoming;
    private bool _incomingStarted;

    [ObservableProperty]
    private string? _summary;

    [ObservableProperty]
    private IReadOnlyList<FieldNode> _fields = [];

    [ObservableProperty]
    private string? _fieldsUnavailable;

    [ObservableProperty]
    private string? _text;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _referencesTotals;

    [ObservableProperty]
    private VirtualRowList<ReferenceRowItem> _referenceRows = ReferenceRowItem.CreateList(null, new ResultSetInfo(Guid.Empty, 0, [new(ReferenceColumns.Field, ResultColumnKind.Text), new(ReferenceColumns.Address, ResultColumnKind.Address, true), new(ReferenceColumns.Type, ResultColumnKind.Text), new(ReferenceColumns.Bytes, ResultColumnKind.Bytes)]));

    [ObservableProperty]
    private VirtualRowList<ReferrerRowItem> _incomingRows = ReferrerRowItem.Empty();

    [ObservableProperty]
    private string? _incomingTotals;

    /// <summary>Why the incoming rows have no field names, or that the incoming references are being indexed.</summary>
    [ObservableProperty]
    private string? _incomingStatus;

    /// <summary>
    /// The references tab shown: 0 outgoing, 1 incoming (sprint 2.1), 2 retention (sprint 2.2). Showing incoming or
    /// retention loads it once.
    /// </summary>
    [ObservableProperty]
    private int _referencesTab;

    /// <summary>The object's key retention paths among the roots found (sprint 2.2).</summary>
    [ObservableProperty]
    private IReadOnlyList<KeyPathItem> _keyPaths = [];

    /// <summary>The merged paths from the object toward its roots: the object itself, holding its holders as children.</summary>
    [ObservableProperty]
    private IReadOnlyList<RetentionNodeItem> _retentionTree = [];

    /// <summary>What the paths say in a line, or why there are none, or the search's progress.</summary>
    [ObservableProperty]
    private string? _retentionStatus;

    /// <summary>Shows every framework step instead of folding runs of them (decision 4).</summary>
    [ObservableProperty]
    private bool _showFrameworkSteps;

    /// <summary>The object's retained size in a line (sprint 2.3), its progress while computed, or why there is none.</summary>
    [ObservableProperty]
    private string? _retainedSummary;

    /// <summary>The immediate dominator, a link (0 when the roots dominate the object or none reaches it).</summary>
    [ObservableProperty]
    private ulong _dominatorAddress;

    /// <summary>The immediate dominator's address and type, the link's text.</summary>
    [ObservableProperty]
    private string? _dominatorText;

    private ObjectPathsResult? _paths;
    private bool _retentionStarted;

    public ObjectViewModel(SnapshotSession session, SnapshotHandle snapshot, ulong address, ILogger? logger = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Address = address;
        _logger = logger ?? NullLogger.Instance;
    }

    public SnapshotHandle Snapshot { get; }

    public ulong Address { get; }

    /// <summary>The navigation title.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Opens a reference row or a field link as a new subject (set by the navigator).</summary>
    public ICommand? OpenCommand { get; set; }

    /// <summary>The load in progress (tests and smoke runs await it).</summary>
    public Task LoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>The incoming references load, once started (tests await it).</summary>
    public Task IncomingTask { get; private set; } = Task.CompletedTask;

    /// <summary>The retention paths load, once started (tests and smoke runs await it).</summary>
    public Task RetentionTask { get; private set; } = Task.CompletedTask;

    /// <summary>The retained size load, started after the retention paths (tests and smoke runs await it).</summary>
    public Task RetainedTask { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the object and its references.</summary>
    public void Start()
    {
        Title = string.Create(CultureInfo.InvariantCulture, $"0x{Address:x}");
        LoadTask = LoadAsync(_lifetime.Token);
    }

    /// <summary>Stops loading and releases the references (the object left the navigation history).</summary>
    public void Close()
    {
        _lifetime.Cancel();
        ReferenceRows.Cancel();
        IncomingRows.Cancel();
        _references?.ReleaseInBackground(_logger);
        _incoming?.ReleaseInBackground(_logger);
    }

    partial void OnReferencesTabChanged(int value)
    {
        if (value == 1 && !_incomingStarted)
        {
            _incomingStarted = true;
            IncomingTask = LoadIncomingAsync(_lifetime.Token);
        }
        else if (value == 2 && !_retentionStarted)
        {
            _retentionStarted = true;
            RetentionTask = LoadRetentionAsync(_lifetime.Token);
            RetainedTask = LoadRetainedAfterAsync(RetentionTask, _lifetime.Token);
        }
    }

    partial void OnShowFrameworkStepsChanged(bool value) => ShowPaths();

    private Task LoadRetentionAsync(CancellationToken cancellationToken)
    {
        RetentionStatus = "Finding the shortest paths from roots (the first query of a snapshot indexes its incoming references)...";
        return SectionLoader.RunAsync(
            "Finding an object's retention paths",
            (progress, ct) => _session.QueryObjectPathsAsync(Snapshot, Address, 10, progress, ct),
            paths =>
            {
                _paths = paths;
                ShowPaths();
                var notes = new List<string>();
                notes.Add(paths.Reachable
                    ? string.Create(CultureInfo.CurrentCulture, $"{paths.RootNodeIds.Count:N0} roots hold this object{(paths.StoppedEarly ? "; the search stopped at its limit, so more may" : string.Empty)}.")
                    : paths.UnreachableNote);
                if (paths.Truncated)
                {
                    notes.Add("Only the closest roots are shown: the others' paths would make the tree too large.");
                }

                if (!paths.CompleteGraph)
                {
                    notes.Add("This snapshot is incomplete, so some paths may be missing.");
                }

                if (paths.FieldsNote is { } why)
                {
                    notes.Add(why);
                }

                RetentionStatus = string.Join(" ", notes);
            },
            status => RetentionStatus = status,
            "The retention paths could not be found. Details are in the log.",
            _logger,
            cancellationToken);
    }

    /// <summary>Loads the retained size once the paths are shown (both use the reverse references index).</summary>
    private async Task LoadRetainedAfterAsync(Task paths, CancellationToken cancellationToken)
    {
        await paths;
        await LoadRetainedAsync(cancellationToken);
    }

    /// <summary>
    /// The object's retained size and immediate dominator (sprint 2.3), after its paths: the first query of a snapshot
    /// computes its dominators, so their phases show meanwhile.
    /// </summary>
    private Task LoadRetainedAsync(CancellationToken cancellationToken)
    {
        RetainedSummary = "Computing retained sizes...";
        return SectionLoader.RunAsync(
            "Reading an object's retained size",
            (progress, ct) => _session.QueryRetainedAsync(Snapshot, Address, progress, ct),
            retained =>
            {
                DominatorAddress = retained.DominatorAddress;
                DominatorText = retained.DominatorAddress == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"0x{retained.DominatorAddress:x} {retained.DominatorType}");
                RetainedSummary = !retained.Reachable
                    ? "Retains nothing. " + retained.UnreachableNote
                    : string.Create(CultureInfo.CurrentCulture, $"Retains {retained.RetainedBytes:N0} bytes ({Format.Bytes(retained.RetainedBytes)}), what it alone keeps alive. ")
                        + (retained.Index.Note is { } note ? note + " " : string.Empty)
                        + (retained.DominatedByRoots ? "Only the roots dominate it: a root holds it, or several reach it on separate paths." : "Dominated by:");
            },
            status => RetainedSummary = status,
            "The retained size could not be computed. Details are in the log.",
            _logger,
            cancellationToken);
    }

    private void ShowPaths()
    {
        if (_paths is null)
        {
            return;
        }

        KeyPaths = [.. _paths.KeyPaths.Select(p => new KeyPathItem(p, ShowFrameworkSteps))];
        RetentionTree = [RetentionNodeItem.BuildTree(_paths.Nodes, ShowFrameworkSteps)];
    }

    // The first query builds the snapshot's index in the worker: its phases replace the busy text (review M7).
    private Task LoadIncomingAsync(CancellationToken cancellationToken)
    {
        IncomingStatus = "Finding referrers (the first query of a snapshot indexes its incoming references)...";
        return SectionLoader.RunAsync(
            "Finding an object's referrers",
            (progress, ct) => _session.QueryReferrersAsync(Snapshot, Address, progress, ct),
            list =>
            {
                _incoming = list.Rows;
                var result = list.Result;
                IncomingRows = ReferrerRowItem.CreateList(list.Rows, result.Rows, ex => IncomingStatus = ex.Message, _logger);
                IncomingTotals = string.Create(CultureInfo.CurrentCulture, $"{result.Rows.TotalCount:N0} objects, {result.ReferenceCount:N0} references")
                    + ReferrersText.Incomplete(result.CompleteGraph);
                IncomingStatus = result.Rows.TotalCount == 0 ? "No object references this one." + ReferrersText.Incomplete(result.CompleteGraph) : result.FieldsNote;
            },
            status => IncomingStatus = status,
            "The referrers could not be read. Details are in the log.",
            _logger,
            cancellationToken,
            list => list.Rows.ReleaseAsync(CancellationToken.None));
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Status = "Reading the object...";
        try
        {
            var details = await _session.GetObjectAsync(Snapshot, Address, progress: null, cancellationToken);
            Title = string.Create(CultureInfo.InvariantCulture, $"{details.TypeName} at 0x{details.Address:x}");
            OnPropertyChanged(nameof(Title));
            Summary = string.Create(
                CultureInfo.CurrentCulture,
                $"{details.TypeName}, {details.Bytes:N0} bytes, {details.HeapKind}{(details.IsRooted ? ", held by a root" : string.Empty)}, {details.ReferenceCount:N0} references");
            Fields = details.Fields is { } fields ? [.. fields.Select(f => new FieldNode(f))] : [];
            FieldsUnavailable = details.FieldsUnavailable;
            Text = details.Text;
            Status = null;

            var references = await _session.QueryReferencesAsync(Snapshot, Address, progress: null, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                await references.ReleaseAsync(CancellationToken.None);
                return;
            }

            _references = references;
            ReferenceRows = ReferenceRowItem.CreateList(references, references.Info, ex => Status = ex.Message, _logger);
            ReferencesTotals = string.Create(CultureInfo.CurrentCulture, $"{references.Info.TotalCount:N0} outgoing references");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Left the history while loading.
        }
        catch (Exception ex) when (ex is WorkerException or OperationCanceledException)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inspecting an object failed");
            Status = "The object could not be read. Details are in the log.";
        }
    }
}
