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

using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Synthetic;
using HeapScope.Worker.Rpc;
using StreamJsonRpc;

namespace HeapScope.Worker.Tests;

public class ResultSetStoreTests
{
    [Fact]
    public void Pages_are_formatted_on_demand_and_capped()
    {
        var store = new ResultSetStore();
        var info = store.Add(new CountingSource(25_000));

        var page = store.TryGetPage(info.Id, 10, 3)!;
        page.Rows.Select(r => r[0]).ShouldBe(["10", "11", "12"]);
        page.TotalCount.ShouldBe(25_000);
        store.TryGetPage(info.Id, 0, 50_000)!.Rows.Count.ShouldBe(WorkerProtocol.MaxPageSize);
        store.TryGetPage(info.Id, 30_000, 5)!.Rows.Count.ShouldBe(0);
    }

    [Fact]
    public void Count_limit_evicts_the_least_recently_used_set_and_disposes_its_owner()
    {
        var store = new ResultSetStore(capacity: 2);
        var firstOwner = new Owner();
        var first = store.Add(new CountingSource(1), firstOwner);
        var second = store.Add(new CountingSource(1));
        store.TryGetPage(first.Id, 0, 1).ShouldNotBeNull();

        store.Add(new CountingSource(1));

        store.TryGetPage(second.Id, 0, 1).ShouldBeNull();
        store.TryGetPage(first.Id, 0, 1).ShouldNotBeNull();
        firstOwner.Disposed.ShouldBeFalse();
    }

    [Fact]
    public void Byte_budget_evicts_older_sets_but_keeps_the_newest()
    {
        var store = new ResultSetStore(capacity: 100, byteBudget: 1_000);
        var ownerA = new Owner();
        var a = store.Add(new CountingSource(1, bytes: 600), ownerA);
        var b = store.Add(new CountingSource(1, bytes: 600));

        store.TryGetPage(a.Id, 0, 1).ShouldBeNull();
        ownerA.Disposed.ShouldBeTrue();
        store.EstimatedBytes.ShouldBe(600);

        var huge = store.Add(new CountingSource(1, bytes: 5_000));
        store.TryGetPage(b.Id, 0, 1).ShouldBeNull();
        store.TryGetPage(huge.Id, 0, 1).ShouldNotBeNull();
        store.Count.ShouldBe(1);
    }

    [Fact]
    public void Release_frees_bytes_and_disposes_the_owner()
    {
        var store = new ResultSetStore();
        var owner = new Owner();
        var info = store.Add(new CountingSource(3, bytes: 300), owner);

        store.Release(info.Id);
        store.Release(info.Id);

        owner.Disposed.ShouldBeTrue();
        store.EstimatedBytes.ShouldBe(0);
        store.TryGetPage(info.Id, 0, 1).ShouldBeNull();
    }

    [Fact]
    public void Type_summary_is_sorted_by_size_and_does_not_need_the_graph_afterwards()
    {
        var builder = new SyntheticGraphBuilder();
        builder.Object("Small", 24);
        builder.Object("Big", 1000);
        builder.Object("Small", 24);
        TypeSummarySource source;
        using (var graph = builder.Build())
        {
            source = TypeSummarySource.Create(graph, TestContext.Current.CancellationToken);
        }

        var cells = new string?[source.Columns.Count];
        source.Count.ShouldBe(2);
        source.FormatRow(0, cells);
        cells.Take(4).ShouldBe(["Big", "1", "1000", "95.42"]);
        source.FormatRow(1, cells);
        cells.Take(4).ShouldBe(["Small", "2", "48", "4.58"]);
        source.EstimatedBytes.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Domain_exceptions_map_to_error_codes()
    {
        WorkerErrors.CodeOf(new CapabilityNotSupportedException(SnapshotCapabilities.FieldValues)).ShouldBe(HeapScopeErrorCode.CapabilityNotSupported);
        WorkerErrors.CodeOf(new ArgumentOutOfRangeException("x")).ShouldBe(HeapScopeErrorCode.InvalidArgument);
        WorkerErrors.CodeOf(new InvalidOperationException()).ShouldBeNull();

        var ex = await Should.ThrowAsync<LocalRpcException>(() => WorkerErrors.GuardAsync<int>(
            () => throw new CapabilityNotSupportedException(SnapshotCapabilities.StringContents)));
        ex.ErrorCode.ShouldBe((int)HeapScopeErrorCode.CapabilityNotSupported);
        await Should.ThrowAsync<OperationCanceledException>(() => WorkerErrors.GuardAsync<int>(() => throw new OperationCanceledException()));
    }

    [Fact]
    public async Task A_set_released_while_a_page_is_formatted_keeps_its_owner_until_the_page_is_done()
    {
        var store = new ResultSetStore();
        var owner = new Owner();
        using var formatting = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var info = store.Add(new BlockingSource(formatting, proceed), owner);

        var page = Task.Run(() => store.TryGetPage(info.Id, 0, 1), TestContext.Current.CancellationToken);
        formatting.Wait(TestContext.Current.CancellationToken);
        store.Release(info.Id);

        owner.Disposed.ShouldBeFalse("the page still reads the snapshot the owner keeps alive");
        proceed.Set();
        (await page).ShouldNotBeNull().Rows.Count.ShouldBe(1);
        owner.Disposed.ShouldBeTrue("the last reader disposes the owner of a released set");
        store.TryGetPage(info.Id, 0, 1).ShouldBeNull();
    }

    /// <summary>Signals when it starts formatting and waits to be let go, like a source reading the dump.</summary>
    private sealed class BlockingSource(ManualResetEventSlim formatting, ManualResetEventSlim proceed) : IResultSource
    {
        public IReadOnlyList<ResultColumn> Columns { get; } = [new("Index", ResultColumnKind.Integer)];

        public int Count => 1;

        public long EstimatedBytes => 100;

        public void FormatRow(int index, string?[] cells)
        {
            formatting.Set();
            proceed.Wait(TimeSpan.FromSeconds(30));
            cells[0] = "0";
        }
    }

    private sealed class CountingSource(int count, long bytes = 100) : IResultSource
    {
        public IReadOnlyList<ResultColumn> Columns { get; } = [new("Index", ResultColumnKind.Integer)];

        public int Count => count;

        public long EstimatedBytes => bytes;

        public void FormatRow(int index, string?[] cells) => cells[0] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class Owner : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
