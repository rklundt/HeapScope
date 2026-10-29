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
    public void The_detectors_are_leak_inspections_and_only_the_growth_detectors_use_a_baseline()
    {
        var detectors = InspectionCatalog.All.Where(i => i.Kind == InspectionKind.Leak).Select(i => i.Name).ToList();

        detectors.ShouldBe(["growing-static-collections", "event-handler-leaks", "timer-leaks", "disposed-but-referenced", "httpclient-instances", "undisposed-service-scopes", "pending-async-methods", "finalizer-backlog", "type-growth"]);
        InspectionCatalog.All.Where(i => i.UsesBaseline).Select(i => i.Name).ShouldBe(["growing-static-collections", "type-growth"]);
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
            var multicast = builder.Object("System.EventHandler", 64, typeKind: TypeKind.Delegate);
            builder.Reference(publisher, multicast);
            reader.Paths[publisher.Address] = new Dictionary<ulong, string> { [multicast.Address] = "Changed" };
            var list = builder.Array("System.Object[]", 600, 8, references: true);
            builder.Reference(multicast, list);
            reader.Fields[(multicast.Address, "_invocationList")] = new ObjectValue(ObjectValueKind.Array) { Address = list.Address, TypeName = "System.Object[]" };
            for (var s = 0; s < 600; s++)
            {
                var handler = builder.Object("System.EventHandler", 64, typeKind: TypeKind.Delegate);
                var subscriber = builder.Object("App.Subscriber", 24);
                builder.Reference(list, handler).Reference(handler, subscriber);
                reader.Fields[(handler.Address, "_target")] = Ref(subscriber, "App.Subscriber");
            }
        }

        // 2,000 delegates in a list's array: a collection, not an event.
        var callbacks = builder.Array("System.Action[]", 2_000, 8, references: true);
        builder.Root(callbacks, detail: "App.Jobs.s_callbacks");
        for (var i = 0; i < 2_000; i++)
        {
            var action = builder.Object("System.Action", 64, typeKind: TypeKind.Delegate);
            builder.Reference(callbacks, action);
        }

        var graph = builder.Build();
        var result = Run("event-handler-leaks", graph, reader, referrers: ReverseReferences.Build(graph, cancellationToken: Ct));

        // Bytes are the subscribers' delegates and targets (review H1): 1,200 x (64 + 24).
        Rows(result).ShouldHaveSingleItem().ShouldBe(["App.Publisher.Changed", "2", "1200", "105600", "App.Subscriber (1,200)"], Compare.Prefix);
        result.Bytes.ShouldBe(105_600);
        result.Basis!.ShouldContain("App.Publisher.Changed 1200 subscribers on 2 publishers");

        // The row opens exactly the objects whose bytes it counts (sprint 3.1, review H1): each subscriber delegate (never the
        // multicast delegate holding the list) and its target, so the set's bytes are the row's.
        var members = result.Rows.MembersOf(0).ShouldNotBeNull().Distinct().ToList();
        members.Count.ShouldBe(2_400);
        members.Select(m => graph.Types.GetName(graph.TypeIndices[m])).Distinct().Order().ShouldBe(["App.Subscriber", "System.EventHandler"]);
        members.Sum(m => (long)graph.GetSize(m)).ShouldBe(result.Bytes);
        members.Where(m => graph.Types.GetName(graph.TypeIndices[m]) == "System.EventHandler").Count(m => graph.GetReferences(m).Length != 1)
            .ShouldBe(0, "each member delegate references only its target: a multicast delegate holding the list is not a member");
    }

    [Fact]
    public void Two_events_of_one_delegate_type_on_a_publisher_are_counted_apart()
    {
        var builder = new SyntheticGraphBuilder();
        var reader = new FakeReader();
        var publisher = builder.Object("App.Service");
        builder.Root(publisher, detail: "App.Service.s_instance");
        var paths = new Dictionary<ulong, string>();
        reader.Paths[publisher.Address] = paths;
        foreach (var (field, count) in new[] { ("Started", 1_100), ("Stopped", 700) })
        {
            var multicast = builder.Object("System.EventHandler", 64, typeKind: TypeKind.Delegate);
            builder.Reference(publisher, multicast);
            paths[multicast.Address] = field;
            var list = builder.Array("System.Object[]", count, 8, references: true);
            builder.Reference(multicast, list);
            reader.Fields[(multicast.Address, "_invocationList")] = new ObjectValue(ObjectValueKind.Array) { Address = list.Address, TypeName = "System.Object[]" };
            for (var s = 0; s < count; s++)
            {
                var handler = builder.Object("System.EventHandler", 64, typeKind: TypeKind.Delegate);
                builder.Reference(list, handler);

                // A static handler: the runtime stores the delegate itself as its target (review M4).
                reader.Fields[(handler.Address, "_target")] = Ref(handler, "System.EventHandler");
            }
        }

        var graph = builder.Build();
        var result = Run("event-handler-leaks", graph, reader, referrers: ReverseReferences.Build(graph, cancellationToken: Ct));

        Rows(result).ShouldHaveSingleItem().ShouldBe(["App.Service.Started", "1", "1100", "70400", "(static method) (1,100)"], Compare.Prefix);
        result.Basis!.ShouldContain("App.Service.Stopped 700 subscribers on 1 publishers");
    }

    [Fact]
    public void A_hand_written_event_in_a_private_field_counts_through_its_invocation_list()
    {
        var builder = new SyntheticGraphBuilder();
        var reader = new FakeReader();
        var publisher = builder.Object("App.Monitor");
        builder.Root(publisher, detail: "App.Monitor.s_instance");
        var multicast = builder.Object("System.EventHandler", 64, typeKind: TypeKind.Delegate);
        builder.Reference(publisher, multicast);
        reader.Paths[publisher.Address] = new Dictionary<ulong, string> { [multicast.Address] = "_changed" };
        var list = builder.Array("System.Object[]", 1_000, 8, references: true);
        builder.Reference(multicast, list);
        reader.Fields[(multicast.Address, "_invocationList")] = new ObjectValue(ObjectValueKind.Array) { Address = list.Address, TypeName = "System.Object[]" };
        for (var s = 0; s < 1_000; s++)
        {
            var handler = builder.Object("System.EventHandler", 64, typeKind: TypeKind.Delegate);
            var subscriber = builder.Object("App.Listener", 24);
            builder.Reference(list, handler).Reference(handler, subscriber);
            reader.Fields[(handler.Address, "_target")] = Ref(subscriber, "App.Listener");

            // A static method marked the runtime's way: a native int read back as a pointer (re-review N1).
            reader.Fields[(handler.Address, "_methodPtrAux")] = new ObjectValue(ObjectValueKind.Pointer) { Unsigned = s % 2 == 0 ? 0UL : 0x7ff0_0000UL };
        }

        var graph = builder.Build();
        var result = Run("event-handler-leaks", graph, reader, referrers: ReverseReferences.Build(graph, cancellationToken: Ct));

        Rows(result).ShouldHaveSingleItem().ShouldBe(["App.Monitor._changed", "1", "1000"], Compare.Prefix);
        Rows(result)[0][4].ShouldBe("(static method) (500), App.Listener (500)");
    }

    [Fact]
    public void Delegates_in_callback_fields_lazies_and_their_own_targets_are_not_events()
    {
        var builder = new SyntheticGraphBuilder();
        var reader = new FakeReader();
        var holder = builder.Array("System.Object[]", 4_800, 8, references: true);
        builder.Root(holder, detail: "App.Holder.s_all");
        for (var i = 0; i < 1_200; i++)
        {
            // Lazy<T>._factory: a framework object's callback, not an event.
            var lazy = builder.Object("System.Lazy<App.Item>", 32);
            var factory = builder.Object("System.Func<App.Item>", 64, typeKind: TypeKind.Delegate);
            builder.Reference(holder, lazy).Reference(lazy, factory);
            reader.Paths[lazy.Address] = new Dictionary<ulong, string> { [factory.Address] = "_factory" };

            // An application object's callback field.
            var job = builder.Object("App.Job", 32);
            var callback = builder.Object("System.Action", 64, typeKind: TypeKind.Delegate);
            builder.Reference(holder, job).Reference(job, callback);
            reader.Paths[job.Address] = new Dictionary<ulong, string> { [callback.Address] = "_onDone" };

            // A subscriber keeping its own handler in a field named like an event.
            var owner = builder.Object("App.Owner", 32);
            var handler = builder.Object("System.EventHandler", 64, typeKind: TypeKind.Delegate);
            builder.Reference(holder, owner).Reference(owner, handler).Reference(handler, owner);
            reader.Paths[owner.Address] = new Dictionary<ulong, string> { [handler.Address] = "Handler" };
            reader.Fields[(handler.Address, "_target")] = Ref(owner, "App.Owner");

            // A class named like a handler that is no delegate.
            var notDelegate = builder.Object("App.OrderCreatedEventHandler", 32);
            var order = builder.Object("App.Order", 32);
            builder.Reference(holder, order).Reference(order, notDelegate);
            reader.Paths[order.Address] = new Dictionary<ulong, string> { [notDelegate.Address] = "Created" };
        }

        var graph = builder.Build();
        var result = Run("event-handler-leaks", graph, reader, referrers: ReverseReferences.Build(graph, cancellationToken: Ct));

        result.Count.ShouldBe(0);
        result.Basis!.ShouldContain("held by its own target 1200");
        result.Basis!.ShouldContain("framework callback field 1200");
        result.Basis!.ShouldContain("field not an event 1200");
    }

    [Fact]
    public void Active_timers_are_counted_by_their_callbacks_target_and_canceled_ones_are_not()
    {
        var builder = new SyntheticGraphBuilder();
        var reader = new FakeReader();
        var queue = builder.Object("System.Threading.TimerQueue");
        builder.Root(queue, detail: "System.Threading.TimerQueue.<Instances>k__BackingField");
        for (var i = 0; i < 503; i++)
        {
            var timer = builder.Object("System.Threading.TimerQueueTimer", 96);
            var callback = builder.Object("System.Threading.TimerCallback", 64, typeKind: TypeKind.Delegate);
            var outer = builder.Object("System.Timers.Timer", 88);
            var elapsed = builder.Object("System.Timers.ElapsedEventHandler", 64, typeKind: TypeKind.Delegate);
            var target = builder.Object("App.Poller", module: "App.dll");
            builder.Reference(queue, timer).Reference(timer, callback).Reference(callback, outer).Reference(outer, elapsed).Reference(elapsed, target);
            Timer(reader, timer, canceled: i == 0, due: i == 1 ? uint.MaxValue : 1_000, period: i == 2 ? uint.MaxValue : 3_600_000);
            reader.Fields[(timer.Address, "_timerCallback")] = Ref(callback, "System.Threading.TimerCallback");
            reader.Fields[(callback.Address, "_target")] = Ref(outer, "System.Timers.Timer");
            reader.Fields[(outer.Address, "_onIntervalElapsed")] = Ref(elapsed, "System.Timers.ElapsedEventHandler");
            reader.Fields[(elapsed.Address, "_target")] = Ref(target, "App.Poller");
        }

        var timers = builder.Build();
        var result = Run("timer-leaks", timers, reader);

        // Bytes are the timers' (review H1): 500 x (96 + 88).
        Rows(result).ShouldHaveSingleItem().ShouldBe(["App.Poller", "500", "92000", "System.Timers.Timer"], Compare.Prefix);

        // The row's members are the timers it counted, so their bytes are the row's (sprint 3.1).
        var members = result.Rows.MembersOf(0).ShouldNotBeNull();
        members.Count.ShouldBe(1_000);
        members.Sum(m => (long)timers.GetSize(m)).ShouldBe(92_000);
        result.Bytes.ShouldBe(92_000);
        result.Basis!.ShouldContain("500 periodic timers in the queue, 1 canceled, 1 paused or fired, 1 one-shot");
    }

    [Fact]
    public void A_timer_on_a_static_callback_belongs_to_its_state_and_one_shot_timers_are_not_counted()
    {
        var builder = new SyntheticGraphBuilder();
        var reader = new FakeReader();
        var queue = builder.Object("System.Threading.TimerQueue");
        builder.Root(queue, detail: "System.Threading.TimerQueue.<Instances>k__BackingField");
        for (var i = 0; i < 1_000; i++)
        {
            var timer = builder.Object("System.Threading.TimerQueueTimer", 96);
            var callback = builder.Object("System.Threading.TimerCallback", 64, typeKind: TypeKind.Delegate);
            var cache = builder.Object("App.Connection+<>c", 24);
            var state = builder.Object("App.Connection", 48, module: "App.dll");
            builder.Reference(queue, timer).Reference(timer, callback).Reference(callback, cache).Reference(timer, state);

            // Half are periodic heartbeats; half one-shot timeouts (Task.Delay, CancelAfter) that release themselves.
            Timer(reader, timer, canceled: false, due: 1_000, period: i % 2 == 0 ? 30_000 : uint.MaxValue);
            reader.Fields[(timer.Address, "_timerCallback")] = Ref(callback, "System.Threading.TimerCallback");
            reader.Fields[(callback.Address, "_target")] = Ref(cache, "App.Connection+<>c");
            reader.Fields[(timer.Address, "_state")] = Ref(state, "App.Connection");
        }

        var result = Run("timer-leaks", builder.Build(), reader);

        var row = Rows(result).ShouldHaveSingleItem();
        row.ShouldBe(["App.Connection", "500", "48000", "System.Threading.Timer", "App.Connection"], Compare.Prefix);
        row[^1].ShouldBe("Reachable", "a row opens the reachable objects only (review H5)");
        result.Basis!.ShouldContain("500 one-shot");
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

        // An integer flag set with Interlocked, and an auto-property's backing field (review M10).
        var connections = builder.Array("App.Connection[]", 240, 8, references: true);
        builder.Root(connections, detail: "App.Pool.s_connections");
        for (var i = 0; i < 240; i++)
        {
            var connection = builder.Object("App.Connection", 40);
            var session = builder.Object("App.Session", 16);
            builder.Reference(connections, connection).Reference(connection, session);
            reader.Fields[(connection.Address, "_disposed")] = Integer(i < 120 ? 1 : 0);
            reader.Fields[(session.Address, "<IsDisposed>k__BackingField")] = new ObjectValue(ObjectValueKind.Boolean) { Boolean = true };
        }

        // Disposed garbage: no root reaches it.
        for (var i = 0; i < 500; i++)
        {
            var garbage = builder.Object("App.Garbage", 32);
            reader.Fields[(garbage.Address, "_disposed")] = new ObjectValue(ObjectValueKind.Boolean) { Boolean = true };
        }

        var graph = builder.Build();
        var result = Run("disposed-but-referenced", graph, reader);

        Rows(result).Select(r => (r[0], r[1], r[3])).ShouldBe([("App.Session", "240", "<IsDisposed>k__BackingField"), ("App.Resource", "150", "_disposed"), ("App.Connection", "120", "_disposed")]);

        // Each row opens its disposed objects only, never the live ones of the type (sprint 3.1, 2.6 review M24).
        var resources = result.Rows.MembersOf(1).ShouldNotBeNull();
        resources.Count.ShouldBe(150);
        resources.Where(m => reader.Fields[(graph.Addresses[m], "_disposed")].Boolean != true).ShouldBeEmpty();
        result.Rows.MembersOf(2).ShouldNotBeNull().Count.ShouldBe(120);
    }

    [Fact]
    public void Many_reachable_http_clients_are_named_and_a_few_are_not()
    {
        var (many, reader) = Clients(owning: 20, sharing: 0);
        var clients = Run("httpclient-instances", many, reader);
        Rows(clients).ShouldHaveSingleItem()[0].ShouldBe("System.Net.Http.HttpClient");
        clients.Rows.MembersOf(0).ShouldNotBeNull().Count.ShouldBe(20, "the clients owning their handler (sprint 3.1)");
        var (few, fewReader) = Clients(owning: 19, sharing: 0);
        Run("httpclient-instances", few, fewReader).Count.ShouldBe(0);
    }

    [Fact]
    public void Http_clients_sharing_a_handler_as_the_factory_makes_them_are_not_counted()
    {
        var (graph, reader) = Clients(owning: 5, sharing: 100);

        var result = Run("httpclient-instances", graph, reader);

        result.Count.ShouldBe(0);
        result.Basis!.ShouldStartWith("5 reachable HttpClients owning their handler (5 distinct handlers), 100 sharing one, 0 unreadable");
    }

    [Fact]
    public void Http_clients_owning_one_shared_handler_are_one_pool_and_not_counted()
    {
        var (graph, reader) = Clients(owning: 30, sharing: 0, sharedHandler: true);

        var result = Run("httpclient-instances", graph, reader);

        result.Count.ShouldBe(0);
        result.Basis!.ShouldStartWith("30 reachable HttpClients owning their handler (1 distinct handlers)");
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

        var rootScope = builder.Object(ScopeType, 72);
        builder.Reference(root, rootScope);
        reader.Fields[(rootScope.Address, "<IsRootScope>k__BackingField")] = new ObjectValue(ObjectValueKind.Boolean) { Boolean = true };
        reader.Fields[(rootScope.Address, "_disposed")] = new ObjectValue(ObjectValueKind.Boolean) { Boolean = false };

        var result = Run("undisposed-service-scopes", builder.Build(), reader);

        Rows(result).ShouldHaveSingleItem().ShouldBe([ScopeType, "550", "39600"], Compare.Prefix);
        result.Rows.MembersOf(0).ShouldNotBeNull().Count.ShouldBe(550, "the open scopes only (sprint 3.1)");
        result.Bytes.ShouldBe(550 * 72);
        result.Basis!.ShouldStartWith("550 reachable scopes not disposed, 50 disposed, 1 root, 0 unreadable; ");
        result.Note!.ShouldContain("requests in flight");
    }

    [Theory]
    [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder<System.Threading.Tasks.VoidTaskResult>+AsyncStateMachineBox<App.Worker+<RunAsync>d__4>", "App.Worker.RunAsync")]
    [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder<System.Int32>+AsyncStateMachineBox<App.Outer+Inner+<GetAsync>d__12>", "App.Outer+Inner.GetAsync")]
    [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder<System.Int32>+AsyncStateMachineBox<App.Machine>", "App.Machine")]
    [InlineData("App.AsyncStateMachineBoxLookalike", null)]
    public void A_state_machine_box_is_named_by_its_async_method(string type, string? method) =>
        AsyncMethods.MethodOf(type).ShouldBe(method);

    [Fact]
    public void Async_methods_waiting_by_the_thousand_are_named_and_finished_or_few_are_not()
    {
        // Sprint 3.5.
        const string Waiting = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder<System.Threading.Tasks.VoidTaskResult>+AsyncStateMachineBox<App.Jobs+<WaitAsync>d__2>";
        const string Finished = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder<System.Int32>+AsyncStateMachineBox<App.Jobs+<GetAsync>d__3>";
        const string Few = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder<System.Threading.Tasks.VoidTaskResult>+AsyncStateMachineBox<App.Jobs+<PollAsync>d__4>";
        var builder = new SyntheticGraphBuilder();
        var reader = new FakeReader();
        var root = builder.Object("App.Jobs");
        builder.Root(root, detail: "App.Jobs.s_pending");
        void Add(string type, int count, long flags)
        {
            for (var i = 0; i < count; i++)
            {
                var box = builder.Object(type, 96);
                builder.Reference(root, box);
                reader.Fields[(box.Address, "m_stateFlags")] = Integer(flags);
            }
        }

        Add(Waiting, 1_200, 0x2000400);
        Add(Finished, 1_500, 0x1000000);
        Add(Few, 999, 0);
        builder.Object(Waiting, 96); // garbage: no root reaches it

        var result = Run("pending-async-methods", builder.Build(), reader);

        Rows(result).ShouldHaveSingleItem().ShouldBe(["App.Jobs.WaitAsync", "1200", "115200", Waiting], Compare.Prefix);
        result.Rows.MembersOf(0).ShouldNotBeNull().Count.ShouldBe(1_200);
        result.Basis!.ShouldStartWith("2199 reachable async state machines waiting in 2 methods, 1500 ended but still referenced, 0 unreadable");
    }

    [Fact]
    public void A_finalizer_backlog_is_named_by_type_and_a_short_queue_is_not()
    {
        // Sprint 3.5.
        static HeapGraph Queue(int items)
        {
            var builder = new SyntheticGraphBuilder();
            for (var i = 0; i < items; i++)
            {
                builder.Root(builder.Object(i % 4 == 0 ? "App.Handle" : "App.Connection", 40), RootKind.FinalizerQueue);
            }

            return builder.Build();
        }

        var result = Run("finalizer-backlog", Queue(1_200), new FakeReader());

        Rows(result).Select(r => (r[0], r[1])).ShouldBe([("App.Connection", "900"), ("App.Handle", "300")]);
        result.Note!.ShouldContain("1,200 objects wait");
        Run("finalizer-backlog", Queue(999), new FakeReader()).Count.ShouldBe(0);
    }

    [Fact]
    public void Types_that_grew_since_the_baseline_are_named_and_framework_or_small_growth_is_not()
    {
        // Sprint 3.5.
        static HeapGraph Census(int orders, int sessions, int strings, int garbage)
        {
            var builder = new SyntheticGraphBuilder();
            var root = builder.Object("App.Store");
            builder.Root(root, detail: "App.Store.s_items");
            foreach (var (type, count) in new[] { ("App.Order", orders), ("App.Session", sessions), ("System.String", strings) })
            {
                for (var i = 0; i < count; i++)
                {
                    builder.Reference(root, builder.Object(type, 32));
                }
            }

            for (var i = 0; i < garbage; i++)
            {
                builder.Object("App.Order", 32);
            }

            return builder.Build();
        }

        var baseline = new InspectionBaseline(Census(orders: 500, sessions: 3_000, strings: 100, garbage: 0), s_info, null);
        var result = Run("type-growth", Census(orders: 2_000, sessions: 4_100, strings: 9_000, garbage: 5_000), new FakeReader(), baseline);

        Rows(result).ShouldHaveSingleItem().ShouldBe(["App.Order", "2000", "500", "1500", "64000"], Compare.Prefix);
        result.Rows.MembersOf(0).ShouldNotBeNull().Count.ShouldBe(2_000, "the reachable orders, not the garbage ones");
        result.Basis!.ShouldContain("largest growth below the thresholds: App.Session 4100 (was 3000)");
        var without = Run("type-growth", Census(2_000, 0, 0, 0), new FakeReader());
        (without.Count, without.Note).ShouldBe((0, "Growth needs a baseline: choose an earlier snapshot of the same process (in the CLI, --baseline)."));
    }

    private static (HeapGraph Graph, FakeReader Reader) Clients(int owning, int sharing, bool sharedHandler = false)
    {
        var builder = new SyntheticGraphBuilder();
        var reader = new FakeReader();
        var holder = builder.Object("App.Service");
        builder.Root(holder, detail: "App.Service.s_instance");
        var shared = builder.Object("System.Net.Http.SocketsHttpHandler", 128);
        builder.Reference(holder, shared);
        for (var i = 0; i < owning + sharing; i++)
        {
            var client = builder.Object("System.Net.Http.HttpClient", 64);
            var handler = sharedHandler ? shared : builder.Object("System.Net.Http.SocketsHttpHandler", 128);
            builder.Reference(holder, client).Reference(client, handler);
            reader.Fields[(client.Address, "_disposeHandler")] = new ObjectValue(ObjectValueKind.Boolean) { Boolean = i < owning };
            reader.Fields[(client.Address, "_handler")] = Ref(handler, "System.Net.Http.SocketsHttpHandler");
        }

        return (builder.Build(), reader);
    }

    private static void Timer(FakeReader reader, SyntheticObject timer, bool canceled, uint due, uint period)
    {
        reader.Fields[(timer.Address, "_canceled")] = new ObjectValue(ObjectValueKind.Boolean) { Boolean = canceled };
        reader.Fields[(timer.Address, "_dueTime")] = new ObjectValue(ObjectValueKind.Unsigned) { Unsigned = due };
        reader.Fields[(timer.Address, "_period")] = new ObjectValue(ObjectValueKind.Unsigned) { Unsigned = period };
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
