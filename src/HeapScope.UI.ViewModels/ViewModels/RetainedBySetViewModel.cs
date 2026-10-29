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
using HeapScope.Contracts;
using HeapScope.UI.Services;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;

/// <summary>
/// "Objects retained by this set" (sprint 2.4): what every object of a type keeps alive together, exactly, by type, next
/// to the type summary's lower bound. A row opens its type's objects.
/// </summary>
public sealed partial class RetainedBySetViewModel : GridQueryViewModel<CellRowItem>
{
    private readonly SnapshotSession _session;
    private bool _queried;

    [ObservableProperty]
    private SnapshotHandle? _snapshot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown), nameof(ShownTitle))]
    private string? _typeName;

    [ObservableProperty]
    private string? _module;

    /// <summary>The set (sprint 3.1) when it is not one type's objects, and its title.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    private ObjectSetQuery? _setQuery;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownTitle))]
    private string? _setTitle;

    /// <summary>The totals: the set, what it retains, and the lower bound, with how the dominators were obtained.</summary>
    [ObservableProperty]
    private string? _summary;

    /// <summary>The visible columns with their index in the cells.</summary>
    [ObservableProperty]
    private IReadOnlyList<(ResultColumn Column, int Index)> _columns = [];

    public RetainedBySetViewModel(SnapshotSession session, IUiDispatcher dispatcher, ILogger<RetainedBySetViewModel>? logger, INavigator? navigator)
        : base(RetainedBySetColumns.Retained, dispatcher, logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        // A row opens the retained objects of its type (sprint 3.1), not every object of the type.
        OpenRowCommand = new RelayCommand<CellRowItem?>(row =>
        {
            if (row?[RetainedBySetColumns.TypeName] is { } typeName && Snapshot is { } snapshot && Retained is { } retained)
            {
                navigator?.Navigate(NavigationTarget.SetInstances(snapshot, retained, RetainedTitle, typeName, row[RetainedBySetColumns.Module]));
            }
        });
        OpenAsSetCommand = new RelayCommand(
            () =>
            {
                if (Snapshot is { } snapshot && Retained is { } retained)
                {
                    navigator?.Navigate(NavigationTarget.ObjectSet(snapshot, retained, RetainedTitle));
                }
            },
            () => IsShown);
    }

    /// <summary>Opens the retained objects as a set with all its views (sprint 3.1).</summary>
    public IRelayCommand OpenAsSetCommand { get; }

    /// <summary>The set shown: <see cref="SetQuery"/>, or the one type's objects.</summary>
    public ObjectSetQuery? Shown => SetQuery ?? (TypeName is { } type ? new ObjectSetQuery(ObjectSetSpec.OfType(type, Module)) : null);

    /// <summary>The set's title: <see cref="SetTitle"/>, or the type.</summary>
    public string ShownTitle => SetTitle ?? TypeName ?? string.Empty;

    private ObjectSetQuery? Retained => Shown is { } shown ? shown with { Spec = ObjectSetSpec.RetainedByOf(shown.Spec) } : null;

    private string RetainedTitle => "Retained by " + ShownTitle;

    /// <summary>The panel's title.</summary>
    public static string Title => ValueBoundTexts.SetAction;

    public bool IsShown => TypeName is not null || SetQuery is not null;

    protected override bool CanQuery => Snapshot is not null && IsShown;

    protected override string BusyText => "Finding what the objects retain together...";

    protected override string EmptyText => "No root reaches any of these objects, so they retain nothing.";

    /// <summary>Asks for <paramref name="typeName"/>'s set (null hides the panel); <see cref="Start"/> queries it.</summary>
    public void Show(SnapshotHandle? snapshot, string? typeName, string? module)
    {
        ClearResults();
        Snapshot = snapshot;
        TypeName = typeName;
        Module = module;
        SetQuery = null;
        SetTitle = null;
        Summary = null;
        _queried = false;
        OpenAsSetCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Asks for what any set's members retain (sprint 3.1); <see cref="Start"/> queries it.</summary>
    public void ShowSet(SnapshotHandle snapshot, ObjectSetQuery set, string title)
    {
        Show(snapshot, (string?)null, null);
        SetQuery = set ?? throw new ArgumentNullException(nameof(set));
        SetTitle = title;
        OpenAsSetCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Queries the set once.</summary>
    public void Start()
    {
        if (CanQuery && !_queried)
        {
            _queried = true;
            if (SetQuery is { } set)
            {
                Logger.LogInformation("Retainers: objects retained by the set {Shape}", set.Spec.Shape);
            }
            else
            {
                Logger.LogInformation("Retainers: objects retained by the set of {Type} ({Module})", TypeName, Module ?? "no module");
            }

            Requery();
        }
    }

    [RelayCommand]
    private void Hide() => Show(Snapshot, (string?)null, null);

    protected override async Task<GridQuery<CellRowItem>> RunQueryAsync(CancellationToken cancellationToken)
    {
        var finished = false;
        var progress = new Progress<JobProgress>(p => Status = finished || cancellationToken.IsCancellationRequested ? Status : ReferrersText.Progress(p));
        var answer = SetQuery is { } set
            ? await _session.QueryRetainedBySetAsync(Snapshot!, set, progress, cancellationToken)
            : await _session.QueryRetainedBySetAsync(Snapshot!, TypeName!, Module, progress, cancellationToken);
        finished = true;
        var result = answer.Result;
        return new GridQuery<CellRowItem>(
            answer.Rows,
            () =>
            {
                Columns = [.. result.Types.Columns.Select((c, i) => (c, i)).Where(c => c.c.Name is not RetainedBySetColumns.TypeName and not RetainedBySetColumns.Module)];
                Summary = Describe(result);
                Totals = string.Create(CultureInfo.CurrentCulture, $"{result.Types.TotalCount:N0} types, {result.Seconds:N1} s");
            },
            onError => CellRowItem.CreateList(answer.Rows, result.Types, onError, Logger));
    }

    /// <summary>The summary: the set (reachable, and how many no root reaches), what it retains exactly, the lower bound.</summary>
    public static string Describe(RetainedBySetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var unreachable = result.AllObjects - result.SetObjects;
        return string.Create(
                CultureInfo.CurrentCulture,
                $"{result.SetObjects:N0} reachable objects of {Format.Bytes(result.SetBytes)}{(unreachable > 0 ? $" ({unreachable:N0} more that no root reaches retain nothing)" : string.Empty)} retain {result.RetainedObjects:N0} objects, {Format.Bytes(result.RetainedBytes)} exactly ({100.0 * result.RetainedBytes / Math.Max(1, result.TotalRetained):N1}% of the reachable heap).{(result.LowerBoundBytes > 0 ? $" The types grid shows at least {Format.Bytes(result.LowerBoundBytes)}: it leaves out what several of them hold together." : string.Empty)} ")
            + RetainedText.Source(result.Dominators);
    }
}
