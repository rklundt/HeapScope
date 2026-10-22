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

        var cells = new string?[3];
        source.Count.ShouldBe(2);
        source.FormatRow(0, cells);
        cells.ShouldBe(["Big", "1", "1000"]);
        source.FormatRow(1, cells);
        cells.ShouldBe(["Small", "2", "48"]);
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
