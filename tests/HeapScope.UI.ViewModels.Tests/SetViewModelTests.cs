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

using System.IO;
using HeapScope.Application;
using HeapScope.Application.Jobs;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>Object sets as subjects (sprint 3.1, ADR-030): the entry points, the views, and the path step.</summary>
public sealed class SetViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly FakeWorkerClient _worker = new();
    private readonly SnapshotsViewModel _snapshots;

    public SetViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        var operations = new WorkerOperations(_worker, new JobTracker(TimeProvider.System));
        _snapshots = new SnapshotsViewModel(new SnapshotSession(_worker, operations.Jobs), operations, new ImmediateUiDispatcher(), new FakeUserInteraction());
        _worker.Service.Rows.Add(["App.Order", "2", "80"]);
        _worker.Service.Rows.Add(["App.Customer", "1", "60"]);
        _worker.Service.InstanceRows.Add(["0x1000", "App.Order", "40", "Gen2", ""]);
        _worker.Service.SetDominatorRows.Add((0x1000, "App.Order", 40, 500, 1));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NavigatorViewModel Navigator => _snapshots.Navigator;

    [Fact]
    public async Task Selected_type_rows_open_as_one_set_whose_views_query_its_members()
    {
        await OpenAsync("app.dmp");
        var types = _snapshots.Types;
        var rows = new List<TypeRowItem> { types.Rows.GetRow(0), types.Rows.GetRow(1) };
        await types.Rows.WhenLoadedAsync();

        types.OpenAsSetCommand.Execute(rows);

        var set = Navigator.Current.ShouldBeOfType<SetViewModel>();
        set.Title.ShouldBe("2 types");
        set.Set.Spec.ShouldBe(ObjectSetSpec.OfTypes([new("App.Order", "App.dll"), new("App.Customer", "App.dll")]), new SpecComparer());
        Navigator.Trail.Select(s => s.Title).ShouldBe(["Types", "2 types"]);
        await set.Types.QueryTask;
        _worker.Service.SummaryQueries[^1].Set.ShouldNotBeNull().Kind.ShouldBe(ObjectSetKind.Types);
        set.Types.IsSet.ShouldBeTrue();

        set.View = SetViews.Instances;
        await set.Instances!.QueryTask;
        var instances = _worker.Service.InstanceQueries.ShouldHaveSingleItem();
        (instances.TypeName, instances.Set?.Kind).ShouldBe((string.Empty, ObjectSetKind.Types));
        set.Instances.Title.ShouldBe("Objects of 2 types");

        set.View = SetViews.Dominators;
        await set.Dominators!.QueryTask;
        _worker.Service.SetDominatorQueries.ShouldHaveSingleItem().Set.Kind.ShouldBe(ObjectSetKind.Types);
        set.Dominators.Summary!.ShouldStartWith("1 of the set's 2 objects are not held by another member");
        set.Dominators.OpenRowCommand!.Execute(await FirstRowAsync(set.Dominators.Rows));
        Navigator.Current.ShouldBeOfType<ObjectViewModel>().Address.ShouldBe(0x1000UL);
    }

    [Fact]
    public async Task What_a_set_retains_opens_on_the_retainers_tab_and_as_a_set_of_its_own()
    {
        _worker.Service.SetRows.Add(("App.Order", 2, 80));
        await OpenAsync("app.dmp");
        var types = _snapshots.Types;
        types.OpenAsSetCommand.Execute(new List<TypeRowItem> { await FirstRowAsync(types.Rows) });
        var set = Navigator.Current.ShouldBeOfType<SetViewModel>();

        set.RetainedCommand.Execute(null);

        _snapshots.Tab.ShouldBe(SnapshotTabs.Retainers);
        var retained = _snapshots.Retainers.Set;
        await retained.QueryTask;
        _worker.Service.SetQueries[^1].Set.ShouldNotBeNull().Kind.ShouldBe(ObjectSetKind.Types);
        Navigator.Trail[^1].Title.ShouldBe("Retained by App.Order");

        retained.OpenAsSetCommand.Execute(null);
        var inner = Navigator.Current.ShouldBeOfType<SetViewModel>();
        (inner.Title, inner.Set.Spec.Kind, inner.Set.Spec.Inner?.Kind).ShouldBe(("Retained by App.Order", ObjectSetKind.RetainedBy, ObjectSetKind.Types));
    }

    [Fact]
    public async Task A_namespace_row_opens_as_a_set_and_two_of_them_say_to_pick_one()
    {
        await OpenAsync("app.dmp");
        var types = _snapshots.Types;
        types.GroupBy = TypeGrouping.Namespace;
        await WaitUntilAsync(() => _worker.Service.SummaryQueries[^1].GroupBy == TypeGrouping.Namespace);
        await types.QueryTask;
        var first = types.Rows.GetRow(0);
        var second = types.Rows.GetRow(1);
        await types.Rows.WhenLoadedAsync();

        types.OpenAsSetCommand.Execute(new List<TypeRowItem> { first, second });
        types.Status!.ShouldStartWith("Open one namespace at a time");
        Navigator.Current.ShouldBeSameAs(types);

        types.OpenRowCommand!.Execute(first);
        var set = Navigator.Current.ShouldBeOfType<SetViewModel>();
        (set.Set.Spec.Kind, set.Set.Spec.Name, set.Title).ShouldBe((ObjectSetKind.Namespace, first.Name, "Namespace " + first.Name));
    }

    [Fact]
    public async Task A_type_row_of_a_set_opens_the_sets_members_of_that_type()
    {
        await OpenAsync("app.dmp");
        var snapshot = _snapshots.Selected!.Handle;
        _snapshots.Navigate(NavigationTarget.ObjectSet(snapshot, new ObjectSetQuery(ObjectSetSpec.OfNamespace("App")), "Namespace App"));
        var set = Navigator.Current.ShouldBeOfType<SetViewModel>();
        await set.Types.QueryTask;

        set.Types.OpenRowCommand!.Execute(await FirstRowAsync(set.Types.Rows));

        var instances = Navigator.Current.ShouldBeOfType<InstancesViewModel>();
        (instances.TypeName, instances.Set?.Spec.Kind, instances.Title).ShouldBe(("App.Order", ObjectSetKind.Namespace, "App.Order in Namespace App"));
        instances.WhyAliveCommand.CanExecute(null).ShouldBeFalse("why alive follows all of a type's objects, not the set's");
        instances.RetainedBySetCommand.CanExecute(null).ShouldBeFalse("part of a set is no set of its own");
    }

    [Fact]
    public async Task Selected_compare_rows_open_their_types_as_a_set_in_the_current_snapshot()
    {
        _worker.Service.DiffRows.Add(["App.Order", "", "1", "2", "1", "40", "80", "40", "1", "1", "App.Order", "App.dll"]);
        await OpenAsync("first.dmp");
        await OpenAsync("second.dmp");
        await _snapshots.Compare.QueryTask;

        _snapshots.Compare.OpenAsSetCommand.Execute(new List<DiffRowItem> { await FirstRowAsync(_snapshots.Compare.Rows) });

        _snapshots.Tab.ShouldBe(SnapshotTabs.Snapshot);
        _snapshots.Selected.ShouldBeSameAs(_snapshots.Compare.Current);
        var set = Navigator.Current.ShouldBeOfType<SetViewModel>();
        set.Set.Spec.Types.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new TypeKeyDto("App.Order", "App.dll"));
    }

    [Fact]
    public async Task Closing_a_set_step_releases_its_views_result_sets()
    {
        await OpenAsync("app.dmp");
        var live = _worker.Service.LiveSets.Count;
        var types = _snapshots.Types;
        types.OpenAsSetCommand.Execute(new List<TypeRowItem> { await FirstRowAsync(types.Rows) });
        var set = Navigator.Current.ShouldBeOfType<SetViewModel>();
        await set.Types.QueryTask;
        set.View = SetViews.Instances;
        await set.Instances!.QueryTask;
        _worker.Service.LiveSets.Count.ShouldBe(live + 2);

        Navigator.CloseStepCommand.Execute(Navigator.CurrentStep);

        await WaitUntilAsync(() => _worker.Service.LiveSets.Count == live);
        Navigator.Current.ShouldBeSameAs(types);
        set.View = SetViews.Dominators;
        set.Dominators.ShouldBeNull("a closed set opens no new views (the 3.1 review)");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task OpenAsync(string name)
    {
        var path = Path.Combine(_folder, name);
        await File.WriteAllBytesAsync(path, [1], Ct);
        await _snapshots.OpenPathAsync(path);
        await _snapshots.Types.QueryTask;
    }

    private static async Task<TRow> FirstRowAsync<TRow>(VirtualRowList<TRow> rows)
        where TRow : class, IIndexedRow
    {
        var row = rows.GetRow(0);
        await rows.WhenLoadedAsync();
        return row;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(10, Ct);
        }
    }

    /// <summary>Specs compare by their cache key (their lists are compared by value).</summary>
    private sealed class SpecComparer : IEqualityComparer<ObjectSetSpec>
    {
        public bool Equals(ObjectSetSpec? x, ObjectSetSpec? y) => x?.CacheKey == y?.CacheKey;

        public int GetHashCode(ObjectSetSpec obj) => obj.CacheKey.GetHashCode(StringComparison.Ordinal);
    }
}
