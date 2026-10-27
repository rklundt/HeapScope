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
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application.Allocations;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.UI.ViewModels;

/// <summary>
/// One allocation trace on the Allocations page (sprint 2.5): its timeline, where dragging selects the interval; the
/// types of the interval; the back traces or call tree; and the top call stacks. Selecting a type narrows the trees and
/// stacks to it. Sizes are estimates from samples, said once under the title.
/// </summary>
public sealed partial class AllocationTraceViewModel : ObservableObject
{
    /// <summary>Stacks listed under Top call stacks.</summary>
    public const int TopStacks = 20;

    private readonly AllocationSession _session;
    private readonly ILogger _logger;
    private CancellationTokenSource? _stacks;

    [ObservableProperty]
    private IReadOnlyList<AllocationBucketDto> _buckets = [];

    [ObservableProperty]
    private double _bucketSeconds = 1;

    /// <summary>The interval selected on the timeline, or null for the whole trace.</summary>
    [ObservableProperty]
    private TimeInterval? _selection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasType))]
    [NotifyCanExecuteChangedFor(nameof(AllTypesCommand))]
    private string? _typeName;

    [ObservableProperty]
    private string? _interval;

    [ObservableProperty]
    private string? _stacksStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFrames))]
    private AllocationStackItem? _selectedStack;

    [ObservableProperty]
    private bool _showCallTree;

    public AllocationTraceViewModel(TraceHandle trace, AllocationSession session, IUiDispatcher dispatcher, ILoggerFactory? loggers = null)
    {
        Trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = loggers?.CreateLogger<AllocationTraceViewModel>() ?? (ILogger)NullLogger.Instance;
        Types = new AllocationTypesViewModel(session, dispatcher, loggers?.CreateLogger<AllocationTypesViewModel>());
        Tree = new AllocationTreeViewModel(session, dispatcher, loggers?.CreateLogger<AllocationTreeViewModel>());
        Types.SelectedTypeChanged += (_, _) =>
        {
            // A query that replaces the rows clears the selection; the type stays until All types.
            if (Types.SelectedType is { } type && type != TypeName)
            {
                using var activity = Act("narrow to the type {0}", type);
                TypeName = type;
                Refresh(types: false);
            }
        };
        var d = trace.Descriptor;
        Summary = string.Create(
            CultureInfo.CurrentCulture,
            $"{d.ProcessName ?? "Process"} ({d.ProcessId}), {d.DurationSeconds:N1} s recorded, about {Format.Bytes(d.SampledBytes)} allocated in {d.Samples:N0} samples");
        Note = AllocationTexts.Sampled + (d.LostEvents > 0 ? " " + AllocationTexts.Lost(d.LostEvents) : string.Empty);
        UpdateInterval();
    }

    public TraceHandle Trace { get; }

    public string Title => Path.GetFileName(Trace.Path);

    public string Summary { get; }

    /// <summary>What the sizes mean, and when the trace undercounts.</summary>
    public string Note { get; }

    public AllocationTypesViewModel Types { get; }

    public AllocationTreeViewModel Tree { get; }

    public ObservableCollection<AllocationStackItem> Stacks { get; } = [];

    public bool HasType => TypeName is not null;

    /// <summary>The selected stack's frames, the allocating method first; none without a selection.</summary>
    public IReadOnlyList<string> SelectedFrames => SelectedStack?.Frames ?? [];

    public double DurationSeconds => Trace.Descriptor.DurationSeconds;

    /// <summary>The timeline and the stacks loading (tests and smoke runs await it).</summary>
    public Task LoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>What the queries are about now.</summary>
    public AllocationScope Scope => new(Selection?.FromSeconds ?? 0, Selection?.ToSeconds, TypeName);

    /// <summary>Loads the timeline, the types, the tree and the stacks of the whole trace.</summary>
    public void Start()
    {
        LoadTask = LoadTimelineAsync();
        Refresh(types: true);
    }

    partial void OnSelectionChanged(TimeInterval? value)
    {
        UpdateInterval();
        using var activity = Act("select the interval {0}", Interval ?? string.Empty);
        Refresh(types: true);
    }

    partial void OnShowCallTreeChanged(bool value) => Tree.Kind = value ? AllocationTreeKindDto.CallTree : AllocationTreeKindDto.BackTraces;

    [RelayCommand]
    private void WholeTrace()
    {
        using var activity = Act("select the whole trace{0}", string.Empty);
        Selection = null;
    }

    [RelayCommand(CanExecute = nameof(HasType))]
    private void AllTypes()
    {
        using var activity = Act("show every type{0}", string.Empty);
        TypeName = null;
        Refresh(types: false);
    }

    private void UpdateInterval() => Interval = Selection is not { } selection
        ? "The whole trace"
        : string.Create(CultureInfo.CurrentCulture, $"From {selection.FromSeconds:N2} s to {selection.ToSeconds:N2} s");

    private void Refresh(bool types)
    {
        var scope = Scope;
        if (types)
        {
            Types.Show(Trace, scope);
        }

        if (Tree.Trace is null)
        {
            Tree.Show(Trace, scope);
        }
        else
        {
            Tree.Narrow(scope);
        }

        LoadStacks(scope);
    }

    private void LoadStacks(AllocationScope scope)
    {
        _stacks?.Cancel();
        _stacks?.Dispose();
        _stacks = new CancellationTokenSource();
        var token = _stacks.Token;
        var load = LoadStacksAsync(scope, token);
        LoadTask = Task.WhenAll(LoadTask, load);
    }

    private async Task LoadStacksAsync(AllocationScope scope, CancellationToken cancellationToken)
    {
        using var activity = new Activity("HeapScope.Query").SetTag("grid", "AllocationStacks").Start();
        StacksStatus = "Finding the top call stacks...";
        try
        {
            var result = await _session.QueryStacksAsync(Trace, scope, TopStacks, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Stacks.Clear();
            foreach (var (stack, rank) in result.Stacks.Select((s, i) => (s, i + 1)))
            {
                Stacks.Add(new AllocationStackItem(rank, stack.Bytes, result.SampledBytes == 0 ? 0 : 100.0 * stack.Bytes / result.SampledBytes, stack.Samples, stack.Frames));
            }

            SelectedStack = Stacks.FirstOrDefault();
            StacksStatus = Stacks.Count == 0 ? "No allocation was sampled in this interval." : null;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // A newer interval or type replaced this list.
        }
        catch (Exception ex) when (ex is WorkerException or OperationCanceledException)
        {
            Stacks.Clear();
            StacksStatus = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The top call stacks failed");
            StacksStatus = "The call stacks failed. Details are in the log.";
        }
    }

    private async Task LoadTimelineAsync()
    {
        // About 200 bars across the trace, at least 10 ms each.
        BucketSeconds = Math.Max(AllocationTreeLimits.MinBucketSeconds, Math.Round(Math.Max(DurationSeconds, 1) / 200, 2));
        try
        {
            var timeline = await _session.QueryTimelineAsync(Trace, BucketSeconds, CancellationToken.None);
            Buckets = timeline.Buckets;
        }
        catch (Exception ex) when (ex is WorkerException or OperationCanceledException)
        {
            _logger.LogInformation("Allocations: the timeline was refused: {Reason}", ex.Message);
            Buckets = [];
        }
    }

    /// <summary>Starts a user action: its log line, under an activity its queries join.</summary>
    private Activity? Act(string action, object argument)
    {
        var activity = new Activity("HeapScope.Allocations").Start();
        _logger.LogInformation("Allocations of {Trace}: {Action}", Title, string.Format(CultureInfo.InvariantCulture, action, argument));
        return activity;
    }
}

/// <summary>An interval of a trace in seconds since it started, <see cref="FromSeconds"/> before <see cref="ToSeconds"/>.</summary>
public sealed record TimeInterval
{
    public TimeInterval(double fromSeconds, double toSeconds)
    {
        // A drag to the left gives the ends in reverse order.
        (FromSeconds, ToSeconds) = fromSeconds <= toSeconds ? (fromSeconds, toSeconds) : (toSeconds, fromSeconds);
        FromSeconds = Math.Max(0, FromSeconds);
    }

    public double FromSeconds { get; }

    public double ToSeconds { get; }
}
