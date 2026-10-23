using Fixtures.Leaks;
using HeapScope.Integration.Tests.Infrastructure;
using Microsoft.Diagnostics.Runtime;

namespace HeapScope.Integration.Tests;

/// <summary>
/// One shared LeakyConsole dump with every leak (few publishers, so multicast invocation lists appear), the semantic
/// fixtures, the runtime kind fixtures and late garbage (sprint 0.4 decision 6).
/// </summary>
public sealed class AllLeaksDump : IAsyncLifetime
{
    public const int Items = 2_000;
    public const int Payload = 32;
    public const int SemanticItems = 500;
    public const int RuntimeItems = 20;
    public const int LateGarbageItems = 300;

    private readonly TempFolder _folder = new();

    public DumpAnalysis Dump { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var path = _folder.File("all-leaks.dmp");
        using (var fixture = await FixtureProcess.StartConsoleAsync(
            "--leak", "all", "--count", Items.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--payload", Payload.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--publishers", "64",
            "--semantics", SemanticItems.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--runtime-semantics", RuntimeItems.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--late-garbage", LateGarbageItems.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            fixture.CaptureDump(path);
        }

        Dump = DumpAnalysis.Open(path);
    }

    public ValueTask DisposeAsync()
    {
        Dump?.Dispose();
        _folder.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Verifies fixtures/KnownAnswers against real dumps with ClrMD (sprint 0.4).</summary>
[Trait("Category", "Integration")]
public sealed class KnownAnswerTests(AllLeaksDump shared) : IClassFixture<AllLeaksDump>
{
    private static readonly string[] s_semanticNames = ["ConditionalWeakTable", "EmbeddedStruct", "LateGarbage", "RuntimeKinds"];

    private const uint FinalizerRunBit = 0x4000_0000;

    private DumpAnalysis Dump => shared.Dump;

    public static TheoryData<string> LeakNames() => [.. LeakCatalog.All.Select(l => l.Name)];

    public static TheoryData<string> RootedNames() => [.. LeakCatalog.All.Select(l => l.Name).Concat(["ConditionalWeakTable", "EmbeddedStruct"])];

    [Fact]
    public void Every_fixture_has_a_known_answer_and_no_answer_is_orphaned()
    {
        var expected = LeakCatalog.All.Select(l => l.Name).Concat(s_semanticNames).Order(StringComparer.Ordinal);

        KnownAnswer.AllNames().Order(StringComparer.Ordinal).ShouldBe(expected);
        foreach (var name in KnownAnswer.AllNames())
        {
            var answer = KnownAnswer.Load(name);
            answer.FormatVersion.ShouldBe(KnownAnswer.CurrentFormatVersion, name);
            answer.Name.ShouldBe(name);
            answer.Types.ShouldNotBeEmpty();
            answer.Kind.ShouldBeOneOf("leak", "semantics");
            if (answer.Kind == "leak")
            {
                answer.Roots.ShouldNotBeNull();
                answer.PathShape.ShouldNotBeNull();
                answer.Census.ShouldNotBeNull();
                answer.RuntimeRoot.ShouldNotBeNull();
                answer.MinRetainedBytesPerItem.ShouldNotBeNull();
            }
        }
    }

    [Theory]
    [MemberData(nameof(LeakNames))]
    public void Leak_instance_counts_match_the_known_answer_exactly(string leak)
    {
        var answer = KnownAnswer.Load(leak);

        var counts = Dump.CountTypes(answer.Types.Select(t => t.Type));

        foreach (var type in answer.Types)
        {
            counts[type.Type].ShouldBe((long)AllLeaksDump.Items * type.PerItem, $"{leak}: {type.Type}");
        }
    }

    [Theory]
    [MemberData(nameof(RootedNames))]
    public void Each_root_reaches_every_item_and_is_the_only_retainer(string name)
    {
        var answer = KnownAnswer.Load(name);
        var expected = answer.Kind == "leak" ? AllLeaksDump.Items : AllLeaksDump.SemanticItems;
        var rootObjects = new HashSet<ulong>();

        foreach (var root in answer.Roots!)
        {
            var value = Dump.ReadStatic(root.DeclaringType, root.Field);
            rootObjects.Add(value.Address);
            var itemType = root.ItemType ?? answer.Types[0].Type;
            Dump.CountReachable(value, itemType, followDependentHandles: true).ShouldBe(expected, $"{root.DeclaringType}.{root.Field}");
        }

        // Exclusivity: with the named roots' values removed, no item is reachable from any retaining root.
        var reachable = Dump.ReachableFromRoots(rootObjects);
        foreach (var itemType in answer.Roots!.Select(r => r.ItemType ?? answer.Types[0].Type).Distinct())
        {
            Dump.Instances(itemType).Count(o => reachable.Contains(o.Address)).ShouldBe(0, $"{itemType} has another retainer");
        }
    }

    [Theory]
    [MemberData(nameof(LeakNames))]
    public void Static_storage_is_rooted_as_recorded(string leak)
    {
        var answer = KnownAnswer.Load(leak);
        var value = Dump.ReadStatic(answer.PrimaryRoot.DeclaringType, answer.PrimaryRoot.Field);

        var origins = Dump.RootOrigins(new HashSet<ulong> { value.Address });

        origins.ShouldContainKey(value.Address, "the static value is not reachable from any retaining root");
        var (rootKind, referrer) = origins[value.Address];
        $"{rootKind} -> {referrer}".ShouldBe($"{answer.RuntimeRoot!.RootKind} -> {answer.RuntimeRoot.Container}");
    }

    [Theory]
    [MemberData(nameof(RootedNames))]
    public void Retention_path_matches_the_path_shape(string name)
    {
        var answer = KnownAnswer.Load(name);
        var root = answer.PrimaryRoot;
        var start = Dump.ReadStatic(root.DeclaringType, root.Field);

        var path = Dump.FindPath(start, root.ItemType ?? answer.Types[0].Type, followDependentHandles: true);

        path.ShouldNotBeNull();
        KnownAnswer.MatchesShape(answer.PathShape!, $"{root.DeclaringType}.{root.Field}", path)
            .ShouldBeTrue($"path {string.Join(" -> ", path)} does not match {string.Join(" -> ", answer.PathShape!)}");
    }

    [Theory]
    [MemberData(nameof(RootedNames))]
    public void Item_retains_at_least_the_known_minimum(string name)
    {
        var answer = KnownAnswer.Load(name);
        var anchor = Dump.Instances(answer.Types[0].Type).First();
        var payload = answer.Kind == "leak" ? AllLeaksDump.Payload : 0;
        var minimum = (ulong)(answer.MinRetainedBytesPerItem!.Base + (answer.MinRetainedBytesPerItem.PayloadMultiplier * payload));

        var retained = Dump.RetainedBytes(anchor);

        retained.ShouldBeGreaterThanOrEqualTo(minimum, $"{answer.Types[0].Type} retains {retained} bytes");
    }

    [Fact]
    public void Census_shallow_sizes_are_as_recorded()
    {
        foreach (var leak in LeakCatalog.All)
        {
            foreach (var entry in KnownAnswer.Load(leak.Name).Census!.Where(c => c.ShallowBytes is not null))
            {
                Dump.Instances(entry.Type).First().Size.ShouldBe((ulong)entry.ShallowBytes!.Value, entry.Type);
            }
        }
    }

    [Fact]
    public void Conditional_weak_table_values_are_retained_only_through_dependent_handles_of_live_keys()
    {
        var counts = Dump.CountTypes(["Fixtures.Leaks.CwtKey", "Fixtures.Leaks.CwtValue"]);
        counts["Fixtures.Leaks.CwtKey"].ShouldBe(AllLeaksDump.SemanticItems, "dead keys must have been collected");
        counts["Fixtures.Leaks.CwtValue"].ShouldBe(AllLeaksDump.SemanticItems, "values of dead keys must have been collected");

        var liveKeys = Dump.ReadStatic("Fixtures.Leaks.SemanticFixtures", "s_liveKeys");

        Dump.CountReachable(liveKeys, "Fixtures.Leaks.CwtValue", followDependentHandles: false).ShouldBe(0);
        Dump.CountReachable(liveKeys, "Fixtures.Leaks.CwtValue", followDependentHandles: true).ShouldBe(AllLeaksDump.SemanticItems);
    }

    [Fact]
    public void Late_garbage_is_present_but_unreachable()
    {
        var answer = KnownAnswer.Load("LateGarbage");
        var reachable = Dump.ReachableFromRoots();

        foreach (var type in answer.PresentButUnreachable!)
        {
            var instances = Dump.Instances(type).ToList();
            instances.Count.ShouldBe(AllLeaksDump.LateGarbageItems, $"{type} present");
            instances.Count(o => reachable.Contains(o.Address)).ShouldBe(0, $"{type} must not be reachable");
        }
    }

    [Fact]
    public void Runtime_kinds_are_as_recorded()
    {
        var answer = KnownAnswer.Load("RuntimeKinds");
        var counts = Dump.CountTypes(answer.Types.Select(t => t.Type));
        foreach (var type in answer.Types)
        {
            counts[type.Type].ShouldBe(AllLeaksDump.RuntimeItems, type.Type);
        }

        foreach (var heapKind in answer.HeapKinds!)
        {
            foreach (var holder in Dump.Instances(heapKind.Type))
            {
                var buffer = holder.ReadObjectField(heapKind.Field);
                Dump.Heap.GetSegmentByAddress(buffer.Address)!.Kind.ToString().ShouldBe(heapKind.HeapKind, heapKind.Type);
            }
        }

        // GC.SuppressFinalize does not remove an object from the finalization queue; it sets the "finalizer run" bit
        // (0x40000000) in the object header word that precedes the method table pointer.
        var registered = Dump.Heap.EnumerateFinalizableObjects().Where(o => o.Type?.Name == "Fixtures.Leaks.FinalizableResource").ToList();
        registered.Count.ShouldBe(AllLeaksDump.RuntimeItems, "every finalizable instance stays registered");
        registered.Count(o => (Dump.ReadObjectHeader(o.Address) & FinalizerRunBit) == 0)
            .ShouldBe(AllLeaksDump.RuntimeItems / 2, "half were suppressed");

        Dump.Runtime.EnumerateHandles().Count(h => h.HandleKind == ClrHandleKind.Pinned && h.Object.Type?.Name == "Fixtures.Leaks.PinnedPoint[]")
            .ShouldBe(AllLeaksDump.RuntimeItems);

        var literal = Dump.ReadStatic("Fixtures.Leaks.RuntimeFixtures", "s_literal");
        Dump.Heap.GetSegmentByAddress(literal.Address)!.Kind.ShouldBe(GCSegmentKind.Frozen);

        var stackRoots = Dump.Heap.EnumerateRoots().Where(r => r.RootKind == ClrRootKind.Stack).ToList();
        var stackTypes = string.Join(" | ", Dump.Runtime.Threads.Select(t => $"thread {t.ManagedThreadId}: " +string.Join(", ", t.EnumerateStackTrace().Select(f => f.Method?.Name ?? f.Kind.ToString())) + " roots: " + string.Join(", ", t.EnumerateStackRoots().Select(r => r.Object.Type?.Name))));
        stackRoots.ShouldContain(r => r.Object.Type!.Name == "Fixtures.Leaks.StackRootedObject", stackTypes);
        stackRoots.ShouldContain(r => r.Object.Type!.Name == "Fixtures.Leaks.InteriorElement[]", stackTypes);
    }
}

/// <summary>A dump with a single leak enabled: the others must allocate nothing.</summary>
[Trait("Category", "Integration")]
public sealed class DisabledLeakTests
{
    [Fact]
    public async Task Disabled_leaks_allocate_no_items()
    {
        using var folder = new TempFolder();
        var path = folder.File("one-leak.dmp");
        using (var fixture = await FixtureProcess.StartConsoleAsync("--leak", "StaticCache", "--count", "300", "--payload", "16"))
        {
            fixture.CaptureDump(path);
        }

        using var dump = DumpAnalysis.Open(path);
        var fixtureTypes = LeakCatalog.All
            .SelectMany(l => KnownAnswer.Load(l.Name).Census!.Where(c => c.Exact).Select(c => (Leak: l.Name, c.Type)))
            .Append((Leak: "EventHandler", Type: "Fixtures.Leaks.LongLivedPublisher"))
            .ToList();

        var counts = dump.CountTypes(fixtureTypes.Select(t => t.Type));

        foreach (var (leak, type) in fixtureTypes)
        {
            counts[type].ShouldBe(leak == "StaticCache" ? 300 : 0, type);
        }
    }
}
