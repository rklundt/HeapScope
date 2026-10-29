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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.UI.ViewModels;

/// <summary>Texts the retention views share (sprint 2.2).</summary>
internal static class RetentionText
{
    /// <summary>A key path, root first, ending at the objects' type; runs of framework steps folded unless shown.</summary>
    public static string Path(KeyPathDto path, bool showFramework)
    {
        var parts = new List<string>();
        var run = 0;
        void Flush()
        {
            if (run > 0)
            {
                parts.Add(run == 1 ? "(1 framework type)" : string.Create(CultureInfo.CurrentCulture, $"({run} framework types)"));
                run = 0;
            }
        }

        foreach (var step in path.Steps)
        {
            if (!showFramework && step.IsFramework)
            {
                run++;
                continue;
            }

            Flush();
            parts.Add(step.TypeName);
        }

        Flush();
        parts.Add(path.TargetType);
        return string.Join(" > ", parts);
    }
}

/// <summary>One key retention path in a list (an object's or a type's), with what opening it shows.</summary>
public sealed class KeyPathItem(KeyPathDto path, bool showFramework) : IObjectLink
{
    public KeyPathDto Path { get; } = path;

    public long Objects => Path.Objects;

    public long Bytes => Path.Bytes;

    public string Root => Path.Root.Description;

    public string PathText { get; } = RetentionText.Path(path, showFramework);

    /// <summary>The example object a row opens.</summary>
    public ulong Address => Path.ExampleAddress;

    public ulong LinkAddress => Address;
}

/// <summary>
/// One object of an object's retention tree (sprint 2.2), shown as "how it holds the next one" plus its type; its children
/// are the objects holding it, toward the roots. A folded run of framework objects is one item whose text counts them.
/// </summary>
public sealed partial class RetentionNodeItem : ObservableObject, IObjectLink
{
    [ObservableProperty]
    private bool _isExpanded = true;

    public RetentionNodeItem(RetentionNodeDto node, int folded, IReadOnlyList<RetentionNodeItem> children)
    {
        ArgumentNullException.ThrowIfNull(node);
        Node = node;
        Children = children;
        var how = node.Field ?? (node.ViaDependentHandle ? "(dependent handle)" : null);

        // A gap node stands for the middle of a long path (its text says how many objects); a folded node for a run of
        // objects of one type (review H1).
        Text = node.Address == 0
            ? node.TypeName
            : (folded > 0 ? string.Create(CultureInfo.CurrentCulture, $"({folded:N0} framework objects) ") : string.Empty)
                + (node.Id == 0 || how is null ? string.Empty : how + " of ")
                + node.TypeName
                + (node.Folded > 0 ? string.Create(CultureInfo.CurrentCulture, $" (and {node.Folded:N0} more of this type)") : string.Empty);
        AddressText = node.Address == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"0x{node.Address:x}");
    }

    public RetentionNodeDto Node { get; }

    public string Text { get; }

    public string AddressText { get; }

    public ulong Address => Node.Address;

    public ulong LinkAddress => Address;

    /// <summary>The root's description where a path starts.</summary>
    public string? Root => Node.Root?.Description;

    public IReadOnlyList<RetentionNodeItem> Children { get; }

    /// <summary>
    /// The tree from node 0 toward the roots; runs of framework objects with one holder each fold into the next object
    /// unless shown (decision 4).
    /// </summary>
    public static RetentionNodeItem BuildTree(IReadOnlyList<RetentionNodeDto> nodes, bool showFramework)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var holders = nodes.Skip(1).ToLookup(n => n.ChildId);

        // Children are built before their parents: deepest nodes first (a path's nodes have increasing depth).
        var built = new Dictionary<int, RetentionNodeItem>();
        foreach (var node in nodes.OrderByDescending(n => n.Depth))
        {
            built[node.Id] = new RetentionNodeItem(node, 0, [.. holders[node.Id].Select(h => Fold(h, holders, built, showFramework))]);
        }

        return built[0];
    }

    private static RetentionNodeItem Fold(RetentionNodeDto holder, ILookup<int, RetentionNodeDto> holders, Dictionary<int, RetentionNodeItem> built, bool showFramework)
    {
        var shown = holder;
        var folded = 0;
        while (!showFramework && shown.IsFramework && holders[shown.Id].Count() == 1)
        {
            folded++;
            shown = holders[shown.Id].Single();
        }

        var item = built[shown.Id];
        return folded == 0 ? item : new RetentionNodeItem(shown, folded, item.Children);
    }
}

/// <summary>
/// Why the objects of a type are alive (sprint 2.2, dotMemory's Key Retention Paths): one row per path from a root, the
/// largest first; opening a row shows its example object. Opened from a type's instances. Since 3.2 also a set's Paths
/// view, and the paths can be shown merged into a diagram.
/// </summary>
public sealed partial class TypePathsViewModel : ObservableObject, INavigationSubject
{
    private readonly SnapshotSession _session;
    private readonly ILogger _logger;
    private readonly INavigator? _navigator;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObjectSetQuery? _set;
    private readonly string? _setTitle;
    private TypePathsResult? _result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Diagram))]
    private IReadOnlyList<KeyPathItem> _paths = [];

    /// <summary>Shows the paths merged into a diagram instead of the list (sprint 3.2).</summary>
    [ObservableProperty]
    private bool _showDiagram;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _totals;

    [ObservableProperty]
    private bool _showFramework;

    public TypePathsViewModel(SnapshotSession session, SnapshotHandle snapshot, string typeName, string? module, ILogger? logger = null, INavigator? navigator = null)
    {
        OpenRowCommand = new RelayCommand<KeyPathItem?>(row =>
        {
            if (row is { Address: not 0 })
            {
                navigator?.Navigate(NavigationTarget.Object(snapshot, row.Address));
            }
        });
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
        Module = module;
        _logger = logger ?? NullLogger.Instance;
        _navigator = navigator;
        SimilarRetentionCommand = new RelayCommand(
            () => _navigator?.Navigate(NavigationTarget.ObjectSet(Snapshot, new ObjectSetQuery(ObjectSetSpec.OfType(TypeName, Module)), TypeName, SetViews.SimilarRetention)),
            () => _set is null);
    }

    /// <summary>The key paths of a set's members (sprint 3.2: the set's Paths view).</summary>
    public TypePathsViewModel(SnapshotSession session, SnapshotHandle snapshot, ObjectSetQuery set, string setTitle, ILogger? logger = null, INavigator? navigator = null)
        : this(session, snapshot, string.Empty, null, logger, navigator)
    {
        _set = set ?? throw new ArgumentNullException(nameof(set));
        _setTitle = setTitle ?? throw new ArgumentNullException(nameof(setTitle));
    }

    public SnapshotHandle Snapshot { get; }

    public string TypeName { get; }

    public string? Module { get; }

    /// <summary>Whether these are a set's paths (no type of their own).</summary>
    public bool IsSet => _set is not null;

    /// <summary>The navigation title.</summary>
    public string Title => "Why alive: " + (_setTitle ?? (string.IsNullOrEmpty(Module) ? TypeName : $"{TypeName} [{Module}]"));

    /// <summary>The paths merged into one graph (sprint 3.2).</summary>
    public KeyPathDiagram Diagram => _result is null ? KeyPathDiagram.Empty : KeyPathDiagram.Build(_result.Paths, ShowFramework, target: _setTitle);

    /// <summary>Opens the type's objects as a set on its Similar Retention view (sprint 3.2).</summary>
    public IRelayCommand SimilarRetentionCommand { get; }

    /// <summary>Opens a row's example object (set by the navigator).</summary>
    public System.Windows.Input.ICommand OpenRowCommand { get; }

    /// <summary>The load in progress (tests and smoke runs await it).</summary>
    public Task LoadTask { get; private set; } = Task.CompletedTask;

    public void Start() => LoadTask = LoadAsync(_lifetime.Token);

    public void Close() => _lifetime.Cancel();

    partial void OnShowFrameworkChanged(bool value)
    {
        ShowPaths();
        OnPropertyChanged(nameof(Diagram));
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Status = "Finding each object's shortest path from a root (the first query of a snapshot walks the whole graph)...";
        var finished = false;
        var progress = new Progress<JobProgress>(p =>
        {
            if (!finished && !cancellationToken.IsCancellationRequested)
            {
                Status = ReferrersText.Progress(p);
            }
        });
        try
        {
            var result = _set is null
                ? await _session.QueryTypePathsAsync(Snapshot, TypeName, Module, 20, progress, cancellationToken)
                : await _session.QuerySetPathsAsync(Snapshot, _set, 20, progress, cancellationToken);
            finished = true;
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _result = result;
            ShowPaths();
            Totals = string.Create(
                CultureInfo.CurrentCulture,
                $"{result.Objects:N0} objects: {result.Objects - result.Unreachable:N0} reachable along {result.Paths.Count + result.OtherPaths:N0} paths, {result.Unreachable:N0} not reachable from any root ({Format.Bytes(result.UnreachableBytes)})")
                + (result.Unreachable > 0 ? ". " + result.UnreachableNote : string.Empty)
                + (result.OtherPaths > 0 ? string.Create(CultureInfo.CurrentCulture, $"; {result.OtherPaths:N0} smaller paths not listed hold {result.OtherObjects:N0} objects") : string.Empty)
                + ReferrersText.Incomplete(result.CompleteGraph).Replace("referrers", "paths", StringComparison.Ordinal);
            Status = result.Objects == 0 ? (_set is null ? "No objects of this type." : "The set has no objects.") : null;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Left the history while loading.
        }
        catch (Exception ex) when (ex is WorkerException or OperationCanceledException)
        {
            finished = true;
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            finished = true;
            _logger.LogError(ex, "Finding a type's retention paths failed");
            Status = "The retention paths could not be found. Details are in the log.";
        }
    }

    private void ShowPaths() => Paths = _result is null ? [] : [.. _result.Paths.Select(p => new KeyPathItem(p, ShowFramework))];
}
