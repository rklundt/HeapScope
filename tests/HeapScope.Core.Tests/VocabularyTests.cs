using System.Runtime.CompilerServices;
using HeapScope.Core.Graph;

namespace HeapScope.Core.Tests;

/// <summary>Values and layouts here are persisted (ARCHITECTURE.md 7) and mirrored on the wire (ADR-016).</summary>
public class VocabularyTests
{
    [Fact]
    public void Persisted_struct_layouts_are_fixed()
    {
        Unsafe.SizeOf<GcRoot>().ShouldBe(12);
        Unsafe.SizeOf<LargeObjectSize>().ShouldBe(16);
        Unsafe.SizeOf<HeapKind>().ShouldBe(1);
        Unsafe.SizeOf<ObjectFlags>().ShouldBe(1);
        Unsafe.SizeOf<TypeKind>().ShouldBe(2);
    }

    [Fact]
    public void Gc_root_round_trips_its_fields()
    {
        var root = new GcRoot(42, RootKind.PinnedHandle, RootFlags.Retaining | RootFlags.Pinned, 7);

        root.ObjectIndex.ShouldBe(42);
        root.Kind.ShouldBe(RootKind.PinnedHandle);
        root.Flags.ShouldBe(RootFlags.Retaining | RootFlags.Pinned);
        root.DetailId.ShouldBe(7);
        root.IsRetaining.ShouldBeTrue();
        root.ShouldBe(new GcRoot(42, RootKind.PinnedHandle, RootFlags.Retaining | RootFlags.Pinned, 7));
    }

    [Fact]
    public void Enum_values_are_stable()
    {
        ((byte)RootKind.WeakHandle).ShouldBe((byte)8);
        ((byte)HeapKind.Frozen).ShouldBe((byte)6);
        ((uint)SnapshotCapabilities.ThreadStacks).ShouldBe(1u << 8);
        ((uint)SnapshotCapabilities.FullDump).ShouldBe(0x1FFu);
    }

    [Fact]
    public void Dependent_handle_is_not_a_root_kind()
    {
        Enum.GetNames<RootKind>().ShouldNotContain(n => n.Contains("Dependent", StringComparison.Ordinal));
    }
}
