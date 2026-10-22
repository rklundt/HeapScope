using HeapScope.Core;

namespace HeapScope.Core.Tests;

public class SnapshotKindTests
{
    [Fact]
    public void Values_are_stable_because_they_are_persisted()
    {
        ((int)SnapshotKind.FullDump).ShouldBe(0);
        ((int)SnapshotKind.GcDump).ShouldBe(1);
    }
}
