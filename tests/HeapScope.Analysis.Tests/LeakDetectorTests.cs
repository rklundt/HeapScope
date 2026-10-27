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

using HeapScope.Analysis.Inspections;
using HeapScope.Core;
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;

namespace HeapScope.Analysis.Tests;

/// <summary>The leak detectors (sprint 2.6) on synthetic graphs with a fake reader: what fires, what does not, and why.</summary>
public sealed class LeakDetectorTests
{
    private const string ScopeType = "Microsoft.Extensions.DependencyInjection.ServiceLookup.ServiceProviderEngineScope";

    private static readonly SnapshotInfo s_info = new(SnapshotKind.FullDump, "synthetic.dmp");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_detectors_are_leak_inspections_and_only_the_static_collections_use_a_baseline()
    {
        var detectors = InspectionCatalog.All.Where(i => i.Kind == InspectionKind.Leak).Select(i => i.Name).ToList();

        detectors.ShouldBe(["growing-static-collections", "event-handler-leaks", "timer-leaks", "disposed-but-referenced", "httpclient-instances", "undisposed-service-scopes"]);
        InspectionCatalog.All.Where(i => i.UsesBaseline).Select(i => i.Name).ShouldBe(["growing-static-collections"]);
        InspectionCatalog.All.Where(i => i.UsesReferrers).Select(i => i.Name).ShouldBe(["event-handler-leaks"]);
        InspectionCatalog.All.Where(i => i.Kind == InspectionKind.Leak).ShouldAllBe(i => i.Parameters.Length > 0);
    }

    [Fact]
    public void A_static_collection_that_grew_since_the_baseline_is_named_by_its_field_and_a_bounded_one_is_not()
    {
        var (baseline, baselineReader) = Statics(cache: 500, bounded: 1_000);
        var (current, reader) = Statics(cache: 4_000, bounded: 1_000);

        var result = Run("growing-static-collections", current, reader, new InspectionBaseline(baseline, s_info, baselineReader));

        result.Count.ShouldBe(1);
        Rows(result).ShouldHaveSingleItem().ShouldBe(["App.Cache.s_items", "System.Collections.Generic.List<App.Item>", "4000", "500", "3500"], Compare.Prefix);
        result.Note.ShouldBeNull();
        result.Basis!.ShouldContain("App.Cache.s_items");
    }

    [Fact]
    public void Without_a_baseline_only_large_static_collections_are_named_and_the_note_says_growth_is_unknown()
    {
        var (graph, reader) = Statics(cache: 12_000, bounded: 1_000);

        var result = Run("growing-static-collections", graph, reader);

        Rows(result).ShouldHaveSingleItem()[0].ShouldBe("App.Cache.s_items");
        result.Note!.ShouldContain("No baseline");
        Run("growing-static-collections", Statics(cache: 9_000, bounded: 1_000).Graph, Statics(cache: 9_000, bounded: 1_000).Reader).Count.ShouldBe(0);
    }

    [Fact]
    public void An_event_holding_many_subscribers_is_named_by_publisher_and_field_and_delegates_in_a_list_are_not_events()
    {
        var builder = new SyntheticGraphBuilder();
        var reader = new FakeReader();
        var holder = builder.Object("App.Holder");
        builder.Root(holder, detail: "App.Holder.s_instance");

        // Two publishers, each with a multicast Changed event of 600 subscribers.
        for (var p = 0; p < 2; p++)
        {
            var publisher = builder.Object("App.Publisher");
            builder.Reference(holder, publisher);
            var multicast = builder.Object("System.EventHandler", 64);
            builder.Reference(publisher, multicast);
            reader.Paths[publisher.Address] = new Dictionary<ulong, string> { [multicast.Address] = "Changed" };
            var list = builder.Array("System.Object[]", 600, 8, references: true);
            builder.Reference(multicast, list);
            reader.Fields[(multicast.Address, "_invocationList")] = new ObjectValue(ObjectValueKind.Array) { Address = list.Address, TypeName = "System.Object[]" };
            for (var s = 0; s < 600; s++)
            {
                var handler = builder.Object("System.EventHandler", 64);
                var subscriber = builder.Object("App.Subscriber");
                builder.Reference(list, handler).Reference(handler, subscriber);
                reader.Fields[(handler.Address, "_target")] = Ref(subscriber, "App.Subscriber");
            }
        }

        // 2,000 delegates in a list's array: a collection, not an event.
        var callbacks = builder.Array("System.Action[]", 2_000, 8, references: true);
        builder.Root(callbacks, detail: "App.Jobs.s_callbacks");
        for (var i = 0; i < 2_000; i++)
        {
            var action = builder.Object("System.Action", 64);
            builder.Reference(callbacks, action);
        }

        var graph = builder.Build();
        var result = Run("event-handler-leaks", graph, reader, referrers: ReverseReferences.Build(graph, cancellationToken: Ct));

        Rows(result).ShouldHaveSingleItem().ShouldBe(["App.Publisher.Changed", "2", "1200", "App.Subscriber (1,200)"], Compare.Prefix);
        result.Basis!.ShouldContain("App.Publisher.Changed 1200 subscribers on 2 publishers");
    }

    [Fact]
    public void Active_timers_are_counted_by_their_callbacks_target_and_canceled_ones_are_not()
    {
        var builder = new SyntheticGraphBuilder();
        var reader = new FakeReader();
        var queue = builder.Object("System.Threading.TimerQueue");
        builder.Root(queue, detail: "System.Threading.TimerQueue.<Instances>k__BackingField");
        for (var i = 0; i < 501; i++)
        {
            var timer = builder.Object("System.Threading.TimerQueueTimer", 96);
            var outer = builder.Object("System.Timers.Timer", 88);
            var elapsed = builder.Object("System.Timers.ElapsedEventHandler", 64);
            var target = builder.Object("App.Poller", module: "App.dll");
            builder.Reference(queue, timer).Reference(timer, outer).Reference(outer, elapsed).Reference(elapsed, target);
            reader.Fields[(timer.Address, "_canceled")] = new ObjectValue(ObjectValueKind.Boolean) { Boolean = i == 0 };
            reader.Fields[(timer.Address, "_timerCallback._target")] = Ref(outer, "System.Timers.Timer");
            reader.Fields[(outer.Address, "_onIntervalElapsed")] = Ref(elapsed, "System.Timers.ElapsedEventHandler");
            reader.Fields[(elapsed.Address, "_target")] = Ref(target, "App.Poller");
        }

        var result = Run("timer-leaks", builder.Build(), reader);

        Rows(result).ShouldHaveSingleItem().ShouldBe(["App.Poller", "500", "System.Timers.Timer"], Compare.Prefix);
        result.Basis!.ShouldContain("500 active timers, 1 canceled");
    }

    [Fact]
    public void Disposed_objects_still_reachable_are_named_and_garbage_or_live_ones_are_not()
    {
        var builder = new SyntheticGraphBuilder();
        var reader = new FakeReader();
        var list = builder.Array("App.Resource[]", 300, 8, references: true);
        builder.Root(list, detail: "App.Pool.s_all");
        for (var i = 0; i < 300; i++)
        {
            var resource = builder.Object("App.Resource", 32);
            builder.Reference(list, resource);

            // 150 disposed and held, 150 alive and in use.
            reader.Fields[(resource.Address, "_disposed")] = new ObjectValue(ObjectValueKind.Boolean) { Boolean = i < 150 };
        }

        // Disposed garbage: no root reaches it.
        for (var i = 0; i < 500; i++)
        {
            var garbage = builder.Object("App.Garbage", 32);
            reader.Fields[(garbage.Address, "_disposed")] = new ObjectValue(ObjectValueKind.Boolean) { Boolean = true };
        }

        var result = Run("disposed-but-referenced", builder.Build(), reader);

        Rows(result).ShouldHaveSingleItem().ShouldBe(["App.Resource", "150", "4800", "_disposed"], Compare.Prefix);
    }

    [Fact]
    public void Many_reachable_http_clients_are_named_and_a_few_are_not()
    {
        Rows(Run("httpclient-instances", Clients(20), new FakeReader())).ShouldHaveSingleItem()[0].ShouldBe("System.Net.Http.HttpClient");
        Run("httpclient-instances", Clients(19), new FakeReader()).Count.ShouldBe(0);
    }

    [Fact]
    public void Undisposed_service_scopes_are_named_and_disposed_ones_are_not_counted()
    {
        var builder = new SyntheticGraphBuilder();
        var reader = new FakeReader();
        var root = builder.Object("App.Requests");
        builder.Root(root, detail: "App.Requests.s_scopes");
        for (var i = 0; i < 600; i++)
        {
            var scope = builder.Object(ScopeType, 72);
            builder.Reference(root, scope);
            reader.Fields[(scope.Address, "_disposed")] = new ObjectValue(ObjectValueKind.Boolean) { Boolean = i >= 550 };
        }

        var result = Run("undisposed-service-scopes", builder.Build(), reader);

        Rows(result).ShouldHaveSingleItem().ShouldBe([ScopeType, "550"], Compare.Prefix);
        result.Basis.ShouldBe("550 reachable scopes not disposed, 50 disposed");
    }

    private static HeapGraph Clients(int count)
    {
        var builder = new SyntheticGraphBuilder();
        var holder = builder.Object("App.Service");
        builder.Root(holder, detail: "App.Service.s_instance");
        for (var i = 0; i < count; i++)
        {
            builder.Reference(holder, builder.Object("System.Net.Http.HttpClient", 64));
        }

        return builder.Build();
    }

    // App.Cache.s_items is a List that grows; App.Lru.s_entries is a Dictionary bounded at its count.
    private static (HeapGraph Graph, FakeReader Reader) Statics(long cache, long bounded)
    {
        var builder = new SyntheticGraphBuilder();
        var reader = new FakeReader();
        var list = builder.Object("System.Collections.Generic.List<App.Item>", 32);
        builder.Root(list, detail: "App.Cache.s_items");
        reader.Fields[(list.Address, "_size")] = Integer(cache);
        var map = builder.Object("System.Collections.Generic.Dictionary<System.Int32, App.Item>", 80);
        builder.Root(map, detail: "App.Lru.s_entries");
        reader.Fields[(map.Address, "_count")] = Integer(bounded + 10);
        reader.Fields[(map.Address, "_freeCount")] = Integer(10);
        var other = builder.Object("App.Settings");
        builder.Root(other, detail: "App.Settings.s_current");
        return (builder.Build(), reader);
    }

    private static InspectionResult Run(string name, HeapGraph graph, FakeReader reader, InspectionBaseline? baseline = null, ReverseReferences? referrers = null) =>
        InspectionCatalog.Find(name)!.Run(new InspectionContext(graph, s_info, reader, null, Ct, baseline, referrers));

    private static List<string?[]> Rows(InspectionResult result)
    {
        var rows = new List<string?[]>();
        for (var i = 0; i < result.Rows.Count; i++)
        {
            var cells = new string?[result.Rows.Columns.Count];
            result.Rows.FormatRow(i, cells);
            rows.Add(cells);
        }

        return rows;
    }

    private static ObjectValue Integer(long value) => new(ObjectValueKind.Integer) { Integer = value };

    private static ObjectValue Ref(SyntheticObject target, string type) => new(ObjectValueKind.Reference) { Address = target.Address, TypeName = type };

    /// <summary>Compares the first cells only.</summary>
    private static class Compare
    {
        public const bool Prefix = true;
    }

    /// <summary>Answers fields and reference paths from tables.</summary>
    private sealed class FakeReader : IObjectReader
    {
        public Dictionary<(ulong Address, string Path), ObjectValue> Fields { get; } = [];

        public Dictionary<ulong, Dictionary<ulong, string>> Paths { get; } = [];

        public IReadOnlyList<ObjectField> ReadFields(ulong address, int maxElements, int maxText, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ObjectValue? ReadField(ulong address, string path, int maxText = 1000) => Fields.GetValueOrDefault((address, path));

        public string? ReadString(ulong address, int maxLength) => null;

        public void ReadStrings(IReadOnlyList<ulong> addresses, int maxLength, Action<int, string?> onEach, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void ReadStringPieces(IReadOnlyList<ulong> addresses, StringPieceHandler onPiece, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IReadOnlyDictionary<ulong, string> ReadReferencePaths(ulong address, int maxElements, CancellationToken cancellationToken) =>
            Paths.GetValueOrDefault(address) ?? [];
    }
}

/// <summary>Row comparisons on leading cells.</summary>
internal static class RowAssertions
{
    public static void ShouldBe(this string?[] row, string[] expected, bool prefix)
    {
        (prefix ? row.Take(expected.Length) : row).ShouldBe(expected);
    }
}
