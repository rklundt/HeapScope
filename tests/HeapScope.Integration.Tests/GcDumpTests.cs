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
using Fixtures.Leaks;
using Graphs;
using HeapScope.Capture;
using HeapScope.Core;
using HeapScope.Core.Graph;
using HeapScope.Core.Loading;
using HeapScope.Integration.Tests.Infrastructure;
using HeapScope.Loaders.GcDump;

namespace HeapScope.Integration.Tests;

/// <summary>One LeakyConsole with every leak, captured as a gcdump and loaded once (sprint 1.8).</summary>
public sealed class AllLeaksGcDump : IAsyncLifetime
{
    public const int Items = 2_000;

    private readonly TempFolder _folder = new();

    public string GcDumpPath { get; private set; } = null!;

    public LoadResult Result { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        GcDumpPath = _folder.File("all-leaks.gcdump");
        using (var fixture = await FixtureProcess.StartConsoleAsync(
            "--leak", "all", "--count", Items.ToString(CultureInfo.InvariantCulture), "--payload", "32", "--publishers", "64"))
        {
            await new GcDumpCapturer().CaptureAsync(Process(fixture.ProcessId), GcDumpPath, null, null, CancellationToken.None);
        }

        Result = new GcDumpLoader().Load(GcDumpPath);
    }

    public ValueTask DisposeAsync()
    {
        Result?.Graph.Dispose();
        _folder.Dispose();
        return ValueTask.CompletedTask;
    }

    public static DotNetProcess Process(int processId) =>
        new ProcessDiscovery().List(CancellationToken.None).Single(p => p.ProcessId == processId);
}

[Trait("Category", "Integration")]
public sealed class GcDumpTests(AllLeaksGcDump shared) : IClassFixture<AllLeaksGcDump>
{
    private HeapGraph Graph => shared.Result.Graph;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> LeakNames() => [.. LeakCatalog.All.Select(l => l.Name)];

    [Fact]
    public void The_capture_writes_a_gcdump_recognized_by_content_and_leaves_no_partial_file()
    {
        SnapshotFileSignature.Detect(shared.GcDumpPath).ShouldBe(SnapshotKind.GcDump);
        File.Exists(shared.GcDumpPath + DumpCapturer.PartialSuffix).ShouldBeFalse();
        new FileInfo(shared.GcDumpPath).Length.ShouldBeGreaterThan(10_000);
    }

    [Fact]
    public void A_complete_gcdump_has_the_graph_and_addresses_but_no_generations_fields_or_strings()
    {
        var result = shared.Result;

        result.Info.Kind.ShouldBe(SnapshotKind.GcDump);
        result.Info.PointerSize.ShouldBe(8);
        result.Capabilities.ShouldBe(
            SnapshotCapabilities.ObjectGraph | SnapshotCapabilities.ObjectAddresses | SnapshotCapabilities.CompleteGraph | SnapshotCapabilities.ExactCounts);
        result.Diagnostics.UnreadableObjects.ShouldBe(0);
        Graph.ObjectCount.ShouldBeGreaterThan(AllLeaksGcDump.Items * 5);
        Graph.EdgeCount.ShouldBeGreaterThan(Graph.ObjectCount / 2);
        Graph.Roots.Count.ShouldBeGreaterThan(0);
        Graph.HeapKinds.Span.ToArray().ShouldAllBe(k => k == HeapKind.Unknown);
    }

    [Theory]
    [MemberData(nameof(LeakNames))]
    public void Leak_instance_counts_match_the_known_answer_exactly(string leak)
    {
        var answer = KnownAnswer.Load(leak);
        var counts = GraphQueries.CountTypes(Graph);

        // A gcdump names nested types without their declaring type (TypeNameForms.AsGcDumpName).
        foreach (var type in answer.Types)
        {
            var name = TypeNameForms.AsGcDumpName(type.Type);
            counts.GetValueOrDefault(name).ShouldBe(AllLeaksGcDump.Items * type.PerItem, $"{leak}: {name}; similar names: {Similar(counts, name)}");
        }

        foreach (var entry in answer.Census!)
        {
            var expected = AllLeaksGcDump.Items * entry.PerItem;
            var name = TypeNameForms.AsGcDumpName(entry.Type);
            var actual = counts.GetValueOrDefault(name);
            if (entry.Exact)
            {
                actual.ShouldBe(expected, $"{leak}: {name}; similar names: {Similar(counts, name)}");
            }
            else
            {
                actual.ShouldBeGreaterThanOrEqualTo(expected, $"{leak}: {name}; similar names: {Similar(counts, name)}");
            }

            if (entry.ShallowBytes is { } bytes)
            {
                Graph.GetSize(GraphQueries.Instances(Graph, name).First()).ShouldBe((ulong)bytes, name);
            }
        }
    }

    [Fact]
    public void Every_leak_item_is_reachable_from_a_retaining_root()
    {
        var reachable = GraphQueries.ReachableFromRetainingRoots(Graph);
        foreach (var leak in LeakCatalog.All)
        {
            var itemType = TypeNameForms.AsGcDumpName(KnownAnswer.Load(leak.Name).Types[0].Type);
            GraphQueries.Instances(Graph, itemType).Count(i => !reachable[i]).ShouldBe(0, itemType);
        }
    }

    [Fact]
    public void Root_kinds_come_from_the_pseudo_nodes()
    {
        var kinds = Graph.Roots.Roots.Span.ToArray().Select(r => r.Kind).ToHashSet();

        // The runtime roots statics through handles to its static storage and the main thread's locals through its stack.
        kinds.ShouldContain(k => k == RootKind.StrongHandle || k == RootKind.PinnedHandle);
        kinds.ShouldContain(RootKind.ThreadStack);
        kinds.ShouldNotContain(RootKind.WeakHandle, "a gcdump records no weak handles");
    }

    [Fact]
    public void Strings_and_arrays_are_marked_by_name()
    {
        var types = Graph.Types;
        var kinds = Enumerable.Range(0, types.Count).GroupBy(i => types.GetName(i)).ToDictionary(g => g.Key, g => types.Kinds[g.First()]);

        kinds["System.String"].ShouldBe(TypeKind.String);
        kinds["System.Byte[]"].ShouldBe(TypeKind.Array);
        kinds.Keys.ShouldNotContain(k => k.Contains("(Bytes >", StringComparison.Ordinal), "size buckets are merged into their type");
    }

    [Fact]
    public void Com_wrappers_load_as_roots_and_real_objects_without_marking_the_gcdump_incomplete()
    {
        // A gcdump written by the vendored writer the way its reader records COM interop (review H4): a CCW pseudo node at
        // the native IUnknown address holding a managed object, and an RCW object with 1,000 extra bytes and a child at its
        // COM object's IUnknown, which is never described.
        using var folder = new TempFolder();
        var path = folder.File("com.gcdump");
        var graph = new MemoryGraph(16) { Is64Bit = true };
        var root = new MemoryNodeBuilder(graph, "[.NET Roots]");
        var managedType = graph.CreateType("Fixtures.Managed", "Leaks.dll");
        var rcwType = graph.CreateType("[RCW System.__ComObject]", "System.Private.CoreLib.dll");
        var ccwType = graph.CreateType("[CCW 0x7000 for type Fixtures.Managed]");
        var managed = graph.GetNodeIndex(0x1000);
        graph.SetNode(managed, managedType, 24, new GrowableArray<NodeIndex>());
        var rcwChildren = new GrowableArray<NodeIndex>();
        rcwChildren.Add(graph.GetNodeIndex(0x9000));
        var rcw = graph.GetNodeIndex(0x2000);
        graph.SetNode(rcw, rcwType, 1032, rcwChildren);
        var ccwChildren = new GrowableArray<NodeIndex>();
        ccwChildren.Add(managed);
        var ccw = graph.GetNodeIndex(0x7000);
        graph.SetNode(ccw, ccwType, 200, ccwChildren);
        root.FindOrCreateChild("[COM/WinRT Objects]").AddChild(ccw);
        root.FindOrCreateChild("[local vars]").AddChild(rcw);
        graph.RootIndex = root.Build();
        graph.AllowReading();
        GCHeapDump.WriteMemoryGraph(graph, path, "test");

        var result = new GcDumpLoader().Load(path, cancellationToken: Ct);
        using var loaded = result.Graph;

        GraphQueries.CountTypes(loaded).ShouldBe(new Dictionary<string, int> { ["Fixtures.Managed"] = 1, ["System.__ComObject"] = 1 });
        result.Capabilities.HasFlag(SnapshotCapabilities.CompleteGraph).ShouldBeTrue("the RCW's COM pointer is not a lost object");
        loaded.TryGetIndex(0x2000, out var rcwIndex).ShouldBeTrue();
        loaded.GetSize(rcwIndex).ShouldBe(32UL);
        loaded.GetReferences(rcwIndex).Length.ShouldBe(0);
        loaded.TryGetIndex(0x1000, out var managedIndex).ShouldBeTrue();
        loaded.Roots.GetRoots(managedIndex).ToArray().ShouldContain(r => r.Kind == RootKind.RefCountedHandle && r.IsRetaining);
        loaded.Roots.GetRoots(rcwIndex).ToArray().ShouldContain(r => r.Kind == RootKind.ThreadStack);
        loaded.TryGetIndex(0x7000, out _).ShouldBeFalse("a CCW is a pseudo node, not an object");
    }

    [Fact]
    public void A_crafted_gcdump_naming_an_unknown_type_is_refused_without_creating_it()
    {
        // A gcdump from elsewhere is untrusted input (ARCHITECTURE.md 12, sprint 1.9): its stream names the types to
        // create. Swap the nested MemoryGraph's type name for a type the reader was never told about, one that records
        // its construction, and check the reader refuses it rather than creating it.
        using var folder = new TempFolder();
        var path = folder.File("crafted.gcdump");
        var bytes = File.ReadAllBytes(shared.GcDumpPath);
        var original = "Graphs.MemoryGraph"u8.ToArray();
        var at = bytes.AsSpan().IndexOf(original);
        at.ShouldBeGreaterThan(4);
        BitConverter.ToInt32(bytes, at - 4).ShouldBe(original.Length, "type names are length-prefixed");
        var canary = System.Text.Encoding.UTF8.GetBytes(typeof(FastSerializationCanary).AssemblyQualifiedName!);
        byte[] crafted = [.. bytes[..(at - 4)], .. BitConverter.GetBytes(canary.Length), .. canary, .. bytes[(at + original.Length)..]];
        File.WriteAllBytes(path, crafted);
        FastSerializationCanary.Created = 0;

        var ex = Should.Throw<SnapshotFormatException>(() => new GcDumpLoader().Load(path, cancellationToken: Ct));
        ex.ToString().ShouldContain("must be registered with the deserializer", customMessage: "TraceEvent resolves only registered factories");

        FastSerializationCanary.Created.ShouldBe(0, "the reader must not create a type the file names");
    }

    [Fact]
    public void A_sampled_gcdump_is_recognized_by_its_multipliers()
    {
        // PerfView writes sampled gcdumps (HeapScope and dotnet-gcdump never do): counts are then scaled estimates and the
        // loader drops CompleteGraph and ExactCounts. The multipliers have internal setters in the vendored reader.
        var dump = new GCHeapDump(shared.GcDumpPath);
        GcDumpLoader.IsSampled(dump).ShouldBeFalse();

        typeof(GCHeapDump).GetProperty(nameof(GCHeapDump.AverageCountMultiplier))!.SetValue(dump, 4f);

        GcDumpLoader.IsSampled(dump).ShouldBeTrue();
    }

    [Fact]
    public async Task Canceling_a_gcdump_capture_leaves_no_file()
    {
        using var folder = new TempFolder();
        using var fixture = await FixtureProcess.StartConsoleAsync("--leak", "StaticCache", "--count", "10");
        var path = folder.File("canceled.gcdump");
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => new GcDumpCapturer().CaptureAsync(AllLeaksGcDump.Process(fixture.ProcessId), path, null, null, canceled.Token));

        Directory.EnumerateFiles(folder.Path).ShouldBeEmpty();
    }

    [Fact]
    public void A_file_that_is_not_a_gcdump_is_refused_with_a_message()
    {
        using var folder = new TempFolder();
        var path = folder.File("text.gcdump");
        File.WriteAllText(path, "not a gcdump");

        var ex = Should.Throw<SnapshotFormatException>(() => new GcDumpLoader().Load(path, cancellationToken: Ct));

        ex.Message.ShouldContain("neither a full memory dump nor a gcdump");
    }

    [Fact]
    public void Reading_a_gcdump_reports_progress_and_stops_on_cancellation()
    {
        var phases = new List<LoadProgress>();
        var result = new GcDumpLoader().Load(shared.GcDumpPath, new SyncProgress(phases.Add), Ct);
        result.Graph.Dispose();
        phases.ShouldContain(p => p.Phase == "Reading gcdump" && p.Total > 0, "review M9");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Should.Throw<OperationCanceledException>(() => new GcDumpLoader().Load(shared.GcDumpPath, cancellationToken: canceled.Token));
    }

    [Fact]
    public void A_truncated_gcdump_is_refused_with_a_message()
    {
        using var folder = new TempFolder();
        var path = folder.File("truncated.gcdump");
        var bytes = File.ReadAllBytes(shared.GcDumpPath);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        var ex = Should.Throw<SnapshotFormatException>(() => new GcDumpLoader().Load(path, cancellationToken: Ct));

        ex.Message.ShouldContain("truncated or damaged");
    }

    [Theory]
    [InlineData("System.Collections.Generic.Dictionary<System.Int32,Fixtures.X>", "System.Collections.Generic.Dictionary<System.Int32, Fixtures.X>")]
    [InlineData("Entry<System.Int32,Fixtures.X>[] (Bytes > 10K)", "Entry<System.Int32, Fixtures.X>[]")]
    [InlineData("[]c__DisplayClass6_0", "<>c__DisplayClass6_0")]
    [InlineData("[]c<System.Char>", "<>c<System.Char>")]
    [InlineData("System.Int32<,>", "System.Int32[,]")]
    [InlineData("System.Collections.Generic.List<System.Int32<,,>>", "System.Collections.Generic.List<System.Int32[,,]>")]
    [InlineData("[RCW System.__ComObject]", "System.__ComObject")]
    [InlineData("System.Byte[] (Bytes > 1M)", "System.Byte[]")]
    [InlineData("System.Byte[][]", "System.Byte[][]")]
    [InlineData("A<B,C<D,E>>", "A<B, C<D, E>>")]
    public void Gcdump_type_names_are_normalized(string recorded, string expected) =>
        TypeNames.Normalize(recorded).ShouldBe(expected);

    [Theory]
    [InlineData("[CCW 0x1f00 for type Fixtures.X]", RootKind.RefCountedHandle, RootFlags.Retaining)]
    [InlineData("[Dependent Handles]", RootKind.Other, RootFlags.Retaining)]
    [InlineData("[pinning Handles]", RootKind.PinnedHandle, RootFlags.Retaining | RootFlags.Pinned)]
    [InlineData("[local vars]", RootKind.ThreadStack, RootFlags.Retaining)]
    public void Pseudo_nodes_map_to_root_kinds(string pseudoNode, RootKind kind, RootFlags flags)
    {
        var root = RootClass.Of(pseudoNode, RootClass.Other);

        (root.Kind, root.Flags).ShouldBe((kind, flags));
    }

    [Fact]
    public void Statics_take_their_field_name_and_value_type_and_thread_statics_stay_other() =>
        (RootClass.Of("[static var System.Collections.Generic.Dictionary<System.Int32,Fixtures.X>.s_cache]", RootClass.Of("[static vars]", RootClass.Other)),
            RootClass.Of("[static var Fixtures.X.t_cache]", RootClass.Of("[thread static vars]", RootClass.Other)))
        .ShouldBe((
            new RootClass(RootKind.StaticField, RootFlags.Retaining, "s_cache (System.Collections.Generic.Dictionary<System.Int32, Fixtures.X>)"),
            new RootClass(RootKind.Other, RootFlags.Retaining, "thread static t_cache (Fixtures.X)")));

    [Fact]
    public void The_static_cache_is_a_static_field_root_named_by_its_field_and_type()
    {
        // .NET 10 reports statics with the value's type, not the declaring type (review M12).
        const string Detail = "s_cache (System.Collections.Generic.Dictionary<System.Int32, Fixtures.Leaks.StaticCacheEntry>)";
        var dictionary = GraphQueries.Instances(Graph, "System.Collections.Generic.Dictionary<System.Int32, Fixtures.Leaks.StaticCacheEntry>").Single();

        Graph.Roots.GetRoots(dictionary).ToArray().ShouldContain(r => r.Kind == RootKind.StaticField && r.IsRetaining && Graph.Strings[r.DetailId] == Detail);
    }

    private sealed class SyncProgress(Action<LoadProgress> report) : IProgress<LoadProgress>
    {
        public void Report(LoadProgress value) => report(value);
    }

    private static string Similar(Dictionary<string, int> counts, string name)
    {
        var tail = name.Split('.', '<')[^1];
        return string.Join(", ", counts.Keys.Where(k => k.Contains(name.Split('<')[0].Split('.')[^1], StringComparison.Ordinal) || k.EndsWith(tail, StringComparison.Ordinal)).Take(5));
    }
}
