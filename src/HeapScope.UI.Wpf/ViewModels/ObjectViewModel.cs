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

namespace HeapScope.UI.Wpf.ViewModels;

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

    /// <summary>The references tab shown: 0 outgoing, 1 incoming (sprint 2.1). Showing incoming loads it once.</summary>
    [ObservableProperty]
    private int _referencesTab;

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
        foreach (var set in new[] { _references, _incoming })
        {
            _ = set?.ReleaseAsync(CancellationToken.None).ContinueWith(
                t => _logger.LogDebug(t.Exception, "Releasing an object's references failed"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    partial void OnReferencesTabChanged(int value)
    {
        if (value == 1 && !_incomingStarted)
        {
            _incomingStarted = true;
            IncomingTask = LoadIncomingAsync(_lifetime.Token);
        }
    }

    private async Task LoadIncomingAsync(CancellationToken cancellationToken)
    {
        IncomingStatus = "Finding referrers (the first query of a snapshot indexes its incoming references)...";
        var finished = false;

        // The first query builds the snapshot's index in the worker: its phases replace the busy text (review M7).
        var progress = new Progress<JobProgress>(p =>
        {
            if (!finished && !cancellationToken.IsCancellationRequested)
            {
                IncomingStatus = ReferrersText.Progress(p);
            }
        });
        try
        {
            var list = await _session.QueryReferrersAsync(Snapshot, Address, progress, cancellationToken);
            finished = true;
            if (cancellationToken.IsCancellationRequested)
            {
                await list.Rows.ReleaseAsync(CancellationToken.None);
                return;
            }

            _incoming = list.Rows;
            var result = list.Result;
            IncomingRows = ReferrerRowItem.CreateList(list.Rows, result.Rows, ex => IncomingStatus = ex.Message, _logger);
            IncomingTotals = string.Create(CultureInfo.CurrentCulture, $"{result.Rows.TotalCount:N0} objects, {result.ReferenceCount:N0} references")
                + ReferrersText.Incomplete(result.CompleteGraph);
            IncomingStatus = result.Rows.TotalCount == 0 ? "No object references this one." + ReferrersText.Incomplete(result.CompleteGraph) : result.FieldsNote;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Left the history while loading.
        }
        catch (Exception ex) when (ex is WorkerStartException or WorkerCrashedException or WorkerOperationException or OperationCanceledException)
        {
            finished = true;
            IncomingStatus = ex.Message;
        }
        catch (Exception ex)
        {
            finished = true;
            _logger.LogError(ex, "Finding an object's referrers failed");
            IncomingStatus = "The referrers could not be read. Details are in the log.";
        }
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
        catch (Exception ex) when (ex is WorkerStartException or WorkerCrashedException or WorkerOperationException or OperationCanceledException)
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
