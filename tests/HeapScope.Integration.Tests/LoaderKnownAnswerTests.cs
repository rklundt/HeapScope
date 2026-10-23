using Fixtures.Leaks;
using HeapScope.Core.Graph;
using HeapScope.Integration.Tests.Infrastructure;
using HeapScope.Loaders.ClrMd;

namespace HeapScope.Integration.Tests;

/// <summary>The shared LeakyConsole dump loaded by HeapScope's own loader once for all loader tests.</summary>
public sealed class LoadedAllLeaksDump : IAsyncLifetime
{
    private readonly AllLeaksDump _dump = new();

    public AllLeaksDump Source => _dump;

    public DumpLoadResult Result { get; private set; } = null!;

    public HeapGraph Graph => Result.Graph;

    public async ValueTask InitializeAsync()
    {
        await _dump.InitializeAsync();
        Result = new ClrMdDumpLoader().Load(_dump.DumpPath);
    }

    public async ValueTask DisposeAsync()
    {
        Result?.Graph.Dispose();
        await _dump.DisposeAsync();
    }
}

/// <summary>
/// The full dump loader against fixtures/KnownAnswers (sprint 1.2): every statement the ClrMD checks of 0.4 verify
/// is verified again on HeapScope's compact graph.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LoaderKnownAnswerTests(LoadedAllLeaksDump shared) : IClassFixture<LoadedAllLeaksDump>
{
    private HeapGraph Graph => shared.Graph;

    public static TheoryData<string> LeakNames() => [.. LeakCatalog.All.Select(l => l.Name)];

    public static TheoryData<string> RootedNames() => [.. LeakCatalog.All.Select(l => l.Name).Concat(["ConditionalWeakTable", "EmbeddedStruct"])];

    [Fact]
    public void Load_reports_a_full_dump_without_dropped_data()
    {
        var result = shared.Result;

        result.Info.Kind.ShouldBe(Core.SnapshotKind.FullDump);
        result.Info.RuntimeVersion.ShouldNotBeNullOrEmpty();
        result.Info.CapturedAtUtc.ShouldNotBeNull();
        result.Capabilities.ShouldBe(Core.SnapshotCapabilities.FullDump);
        Graph.DroppedReferenceCount.ShouldBe(0);
        Graph.DroppedRootCount.ShouldBe(0);
        result.Diagnostics.UnreadableObjects.ShouldBe(0);
        result.Diagnostics.DebuggableModules.ShouldNotContain("Leaks.dll", "fixtures are always optimized");
    }

    [Fact]
    public void Object_and_reference_counts_equal_clrmd()
    {
        var dump = shared.Source.Dump;
        long objects = 0, references = 0;
        ulong size = 0;
        foreach (var obj in dump.Heap.EnumerateObjects())
        {
            if (!obj.IsFree)
            {
                objects++;
                size += obj.Size;
                references += obj.EnumerateReferenceAddresses(carefully: false, considerDependantHandles: false).Count();
            }
        }

        var dependentEdges = dump.Runtime.EnumerateHandles()
            .Count(h => h.HandleKind == Microsoft.Diagnostics.Runtime.ClrHandleKind.Dependent && !h.Object.IsNull && !h.Dependent.IsNull);

        Graph.ObjectCount.ShouldBe((int)objects);
        Graph.TotalSize.ShouldBe(size);
        ((long)Graph.EdgeCount).ShouldBe(references + dependentEdges);
    }

    [Fact]
    public void Static_storage_is_rooted_once_and_finalizer_roots_are_only_the_f_reachable_queue()
    {
        var roots = Graph.Roots.Roots.Span.ToArray();
        roots.Where(r => r.Kind != RootKind.ThreadStack).GroupBy(r => (r.ObjectIndex, r.Kind, r.DetailId)).Where(g => g.Count() > 1)
            .ShouldBeEmpty("no duplicate handle or static roots (several stack slots may hold one object)");
        roots.Where(r => r.Kind == RootKind.StrongHandle)
            .GroupBy(r => r.ObjectIndex)
            .Where(g => g.Count() > 1)
            .ShouldBeEmpty("the static storage bucket is rooted once, not as both a strong handle and \"static storage\"");

        // Registered-only finalizable objects (HasFinalizer) are not roots; only the f-reachable queue is.
        var fReachable = shared.Source.Dump.Heap.EnumerateFinalizerRoots().Select(r => r.Object.Address).ToHashSet();
        foreach (var root in roots.Where(r => r.Kind == RootKind.FinalizerQueue))
        {
            fReachable.ShouldContain(Graph.Addresses[root.ObjectIndex]);
        }

        var registeredOnly = GraphQueries.Instances(Graph, "Fixtures.Leaks.FinalizableResource")
            .Where(i => (Graph.Flags[i] & ObjectFlags.HasFinalizer) != 0)
            .ToList();
        registeredOnly.ShouldNotBeEmpty();
        registeredOnly.Where(i => Graph.Roots.GetRoots(i).ToArray().Any(r => r.Kind == RootKind.FinalizerQueue)).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(LeakNames))]
    public void Leak_instance_counts_match_the_known_answer_exactly(string leak)
    {
        var answer = KnownAnswer.Load(leak);
        var counts = GraphQueries.CountTypes(Graph);

        foreach (var type in answer.Types)
        {
            counts.GetValueOrDefault(type.Type).ShouldBe(AllLeaksDump.Items * type.PerItem, $"{leak}: {type.Type}");
        }

        foreach (var entry in answer.Census!)
        {
            var expected = AllLeaksDump.Items * entry.PerItem;
            if (entry.Exact)
            {
                counts.GetValueOrDefault(entry.Type).ShouldBe(expected, $"{leak}: {entry.Type}");
            }
            else
            {
                counts.GetValueOrDefault(entry.Type).ShouldBeGreaterThanOrEqualTo(expected, $"{leak}: {entry.Type}");
            }

            if (entry.ShallowBytes is { } bytes)
            {
                Graph.GetSize(GraphQueries.Instances(Graph, entry.Type).First()).ShouldBe((ulong)bytes, entry.Type);
            }
        }
    }

    [Theory]
    [MemberData(nameof(RootedNames))]
    public void Static_field_roots_name_the_field_and_hold_the_static_value(string name)
    {
        var answer = KnownAnswer.Load(name);
        foreach (var root in answer.Roots!)
        {
            var detail = $"{root.DeclaringType}.{root.Field}";
            var value = shared.Source.Dump.ReadStatic(root.DeclaringType, root.Field);
            Graph.TryGetIndex(value.Address, out var index).ShouldBeTrue(detail);

            var roots = Graph.Roots.GetRoots(index).ToArray();
            roots.ShouldContain(r => r.Kind == RootKind.StaticField && r.IsRetaining && Graph.Strings[r.DetailId] == detail, detail);
        }
    }

    [Theory]
    [MemberData(nameof(LeakNames))]
    public void Static_storage_is_rooted_as_recorded(string leak)
    {
        var answer = KnownAnswer.Load(leak);
        var value = shared.Source.Dump.ReadStatic(answer.PrimaryRoot.DeclaringType, answer.PrimaryRoot.Field);
        Graph.TryGetIndex(value.Address, out var valueIndex).ShouldBeTrue();
        var expectedKind = Enum.Parse<RootKind>(answer.RuntimeRoot!.RootKind);

        var containers = GraphQueries.Referrers(Graph, valueIndex)
            .Where(i => Graph.Types.GetName(Graph.TypeIndices[i]) == answer.RuntimeRoot.Container)
            .Where(i => Graph.Roots.GetRoots(i).ToArray().Any(r => r.Kind == expectedKind && r.IsRetaining))
            .ToList();

        containers.ShouldNotBeEmpty($"no retaining {expectedKind} root on a {answer.RuntimeRoot.Container} holding the static value");
    }

    [Theory]
    [MemberData(nameof(RootedNames))]
    public void Retention_path_matches_the_path_shape(string name)
    {
        var answer = KnownAnswer.Load(name);
        var root = answer.PrimaryRoot;
        var value = shared.Source.Dump.ReadStatic(root.DeclaringType, root.Field);
        Graph.TryGetIndex(value.Address, out var start).ShouldBeTrue();

        var path = GraphQueries.FindPath(Graph, start, root.ItemType ?? answer.Types[0].Type);

        path.ShouldNotBeNull();
        // The graph has no marker for dependent-handle edges: they are ordinary edges (ARCHITECTURE.md 6.1).
        var shape = answer.PathShape!.Where(s => s != DumpAnalysis.DependentHandleStep).ToList();
        KnownAnswer.MatchesShape(shape, $"{root.DeclaringType}.{root.Field}", path)
            .ShouldBeTrue($"path {string.Join(" -> ", path)} does not match {string.Join(" -> ", shape)}");
    }

    [Fact]
    public void Every_leak_item_is_reachable_from_a_retaining_root()
    {
        var reachable = GraphQueries.ReachableFromRetainingRoots(Graph);
        foreach (var leak in LeakCatalog.All)
        {
            var itemType = KnownAnswer.Load(leak.Name).Types[0].Type;
            GraphQueries.Instances(Graph, itemType).Count(i => !reachable[i]).ShouldBe(0, itemType);
        }
    }

    [Fact]
    public void Conditional_weak_table_values_are_edges_from_their_keys_and_never_roots()
    {
        var values = GraphQueries.Instances(Graph, "Fixtures.Leaks.CwtValue").ToHashSet();
        values.Count.ShouldBe(AllLeaksDump.SemanticItems);

        var keysWithValueEdge = GraphQueries.Instances(Graph, "Fixtures.Leaks.CwtKey")
            .Count(k => Graph.GetReferences(k).ToArray().Any(values.Contains));
        keysWithValueEdge.ShouldBe(AllLeaksDump.SemanticItems);

        values.Where(v => Graph.Roots.GetRoots(v).Length > 0).ShouldBeEmpty("a dependent-handle secondary must never be a root");
    }

    [Fact]
    public void Late_garbage_is_present_but_unreachable()
    {
        var answer = KnownAnswer.Load("LateGarbage");
        var reachable = GraphQueries.ReachableFromRetainingRoots(Graph);

        foreach (var type in answer.PresentButUnreachable!)
        {
            var instances = GraphQueries.Instances(Graph, type).ToList();
            instances.Count.ShouldBe(AllLeaksDump.LateGarbageItems, $"{type} present");
            instances.Count(i => reachable[i]).ShouldBe(0, $"{type} must not be reachable");
        }

        // Weak-only targets carry a weak handle root that does not retain.
        GraphQueries.Instances(Graph, "Fixtures.Leaks.WeakOnlyTarget")
            .Count(i => Graph.Roots.GetRoots(i).ToArray().Any(r => r.Kind == RootKind.WeakHandle && !r.IsRetaining))
            .ShouldBe(AllLeaksDump.LateGarbageItems);
    }

    [Fact]
    public void Runtime_kinds_are_as_recorded()
    {
        var items = AllLeaksDump.RuntimeItems;
        HeapKindOfReferencedBuffers("Fixtures.Leaks.PohHolder").Count(k => k == HeapKind.PinnedObjectHeap).ShouldBe(items);
        HeapKindOfReferencedBuffers("Fixtures.Leaks.LohHolder").Count(k => k == HeapKind.LargeObjectHeap).ShouldBe(items);

        var finalizable = GraphQueries.Instances(Graph, "Fixtures.Leaks.FinalizableResource").ToList();
        finalizable.Count.ShouldBe(items);
        finalizable.Count(i => (Graph.Flags[i] & ObjectFlags.HasFinalizer) != 0).ShouldBe(items / 2, "half were suppressed");

        GraphQueries.Instances(Graph, "Fixtures.Leaks.PinnedPoint[]")
            .Count(i => Graph.Roots.GetRoots(i).ToArray().Any(r => r.Kind == RootKind.PinnedHandle && (r.Flags & RootFlags.Pinned) != 0))
            .ShouldBe(items);

        var literal = shared.Source.Dump.ReadStatic("Fixtures.Leaks.RuntimeFixtures", "s_literal");
        Graph.TryGetIndex(literal.Address, out var literalIndex).ShouldBeTrue();
        Graph.HeapKinds[literalIndex].ShouldBe(HeapKind.Frozen);

        StackRoots("Fixtures.Leaks.StackRootedObject").ShouldContain(r => Graph.Strings[r.DetailId]!.StartsWith("thread ", StringComparison.Ordinal));
        StackRoots("Fixtures.Leaks.InteriorElement[]").ShouldContain(r => (r.Flags & RootFlags.Interior) != 0, "held only through a ref to an element");

        // Every object outside the fixed-kind segments has a generation.
        Graph.HeapKinds.Span.ToArray().ShouldNotContain(HeapKind.Unknown);
    }

    [Fact]
    public void A_dump_whose_runtime_is_not_on_this_machine_fails_with_the_version_and_without_downloading()
    {
        using var folder = new TempFolder();
        var options = new ClrMdLoaderOptions { InstalledRuntimeRoots = [folder.Path], SearchDumpedRuntimeDirectory = false };

        var exception = Should.Throw<RuntimeComponentMissingException>(() => new ClrMdDumpLoader(options).Load(shared.Source.DumpPath));

        exception.RuntimeVersion.ShouldBe(shared.Result.Info.RuntimeVersion);
        exception.Searched.ShouldContain(s => s.StartsWith(folder.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_dump_from_another_machine_finds_the_installed_runtime_by_its_image()
    {
        // The runtime folder recorded in the dump is ignored; the installed runtime folders are searched by PE match.
        var options = new ClrMdLoaderOptions { SearchDumpedRuntimeDirectory = false };

        var result = new ClrMdDumpLoader(options).Load(shared.Source.DumpPath, cancellationToken: TestContext.Current.CancellationToken);

        result.Graph.ObjectCount.ShouldBe(Graph.ObjectCount);
        result.Graph.Dispose();
    }

    [Fact]
    public void A_minidump_without_the_heap_is_refused_with_how_to_capture_a_full_dump()
    {
        using var folder = new TempFolder();
        var path = folder.File("mini.dmp");
        var bytes = File.ReadAllBytes(shared.Source.DumpPath).AsSpan(0, 64 * 1024).ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), 0x0);
        File.WriteAllBytes(path, bytes);

        var exception = Should.Throw<Core.SnapshotFormatException>(() => new ClrMdDumpLoader().Load(path));

        exception.Message.ShouldContain("without the managed heap");
        exception.Message.ShouldContain("--type Full");
    }

    [Fact]
    public async Task A_real_mini_dump_is_refused()
    {
        using var folder = new TempFolder();
        var path = folder.File("normal.dmp");
        using (var fixture = await FixtureProcess.StartConsoleAsync("--leak", "StaticCache", "--count", "10"))
        {
            fixture.CaptureDump(path, Microsoft.Diagnostics.NETCore.Client.DumpType.Normal);
        }

        var exception = Should.Throw<Core.SnapshotFormatException>(() => new ClrMdDumpLoader().Load(path, cancellationToken: TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("without the managed heap");
    }

    [Fact]
    public void A_truncated_dump_is_refused_or_loses_complete_and_exact()
    {
        using var folder = new TempFolder();
        var path = folder.File("truncated.dmp");
        using (var source = File.OpenRead(shared.Source.DumpPath))
        using (var target = File.Create(path))
        {
            var buffer = new byte[source.Length * 6 / 10];
            source.ReadExactly(buffer);
            target.Write(buffer);
        }

        try
        {
            var result = new ClrMdDumpLoader().Load(path, cancellationToken: TestContext.Current.CancellationToken);
            result.Graph.Dispose();
            result.Capabilities.HasFlag(Core.SnapshotCapabilities.CompleteGraph).ShouldBeFalse();
            result.Capabilities.HasFlag(Core.SnapshotCapabilities.ExactCounts).ShouldBeFalse();
        }
        catch (Exception ex) when (ex is Core.SnapshotFormatException or RuntimeComponentMissingException)
        {
            // Refusing a damaged dump with a message is also correct.
        }
    }

    [Fact]
    public void Shared_fixture_dumps_carry_heap_memory_and_load_complete()
    {
        MinidumpHeader.Read(shared.Source.DumpPath).HasHeapMemory.ShouldBeTrue();
        shared.Result.Capabilities.HasFlag(Core.SnapshotCapabilities.CompleteGraph).ShouldBeTrue();
        shared.Result.Diagnostics.IncompleteSegments.ShouldBe(0);
    }

    [Fact]
    public void A_file_that_is_not_a_dump_fails_with_a_format_error()
    {
        using var folder = new TempFolder();
        var path = folder.File("not-a-dump.dmp");
        File.WriteAllBytes(path, new byte[64 * 1024]);

        Should.Throw<Core.SnapshotFormatException>(() => new ClrMdDumpLoader().Load(path));
    }

    [Fact]
    public void Cancellation_stops_the_load_within_two_seconds()
    {
        using var cancellation = new CancellationTokenSource();
        var canceledAt = System.Diagnostics.Stopwatch.StartNew();
        var progress = new SynchronousProgress(p =>
        {
            if (p.Phase == "Reading references" && !cancellation.IsCancellationRequested)
            {
                canceledAt.Restart();
                cancellation.Cancel();
            }
        });

        Should.Throw<OperationCanceledException>(() => new ClrMdDumpLoader().Load(shared.Source.DumpPath, progress, cancellation.Token));

        cancellation.IsCancellationRequested.ShouldBeTrue();
        canceledAt.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    private sealed class SynchronousProgress(Action<LoadProgress> report) : IProgress<LoadProgress>
    {
        public void Report(LoadProgress value) => report(value);
    }

    private List<HeapKind> HeapKindOfReferencedBuffers(string holderType) =>
        [.. GraphQueries.Instances(Graph, holderType).SelectMany(h => Graph.GetReferences(h).ToArray()).Select(t => Graph.HeapKinds[t])];

    private List<GcRoot> StackRoots(string type) =>
        [.. GraphQueries.Instances(Graph, type).SelectMany(i => Graph.Roots.GetRoots(i).ToArray()).Where(r => r.Kind == RootKind.ThreadStack)];
}
