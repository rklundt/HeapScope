using System.Diagnostics;
using System.Globalization;
using HeapScope.Integration.Tests.Infrastructure;
using HeapScope.Loaders.ClrMd;

namespace HeapScope.Integration.Tests;

/// <summary>
/// A dense, realistic heap (the DenseHeap fixture: real Roslyn compiler objects, about 3 references per object) loaded
/// by HeapScope's loader and checked against ClrMD (sprint 1.2). The Medium-size run is the
/// <c>New-DenseDump.ps1</c> script plus the <c>load-dump</c> benchmark; this test uses a small heap.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DenseHeapTests : IDisposable
{
    private const int TargetMb = 200;

    private readonly TempFolder _folder = new();

    [Fact]
    public async Task A_dense_real_library_heap_loads_completely_with_the_expected_density()
    {
        var path = _folder.File("dense.dmp");
        using (var fixture = await FixtureProcess.StartDenseAsync(TargetMb))
        {
            fixture.CaptureDump(path);
        }

        var result = new ClrMdDumpLoader().Load(path, cancellationToken: TestContext.Current.CancellationToken);
        using var graph = result.Graph;
        using var dump = DumpAnalysis.Open(path);
        long objects = 0, references = 0;
        foreach (var obj in dump.Heap.EnumerateObjects())
        {
            if (!obj.IsFree)
            {
                objects++;
                references += obj.EnumerateReferenceAddresses(carefully: false, considerDependantHandles: false).Count();
            }
        }

        var dependentEdges = dump.Runtime.EnumerateHandles()
            .Count(h => h.HandleKind == Microsoft.Diagnostics.Runtime.ClrHandleKind.Dependent && !h.Object.IsNull && !h.Dependent.IsNull);

        graph.ObjectCount.ShouldBe((int)objects);
        ((long)graph.EdgeCount).ShouldBe(references + dependentEdges, "every reference ClrMD reports, plus one edge per live dependent handle");
        graph.DroppedReferenceCount.ShouldBe(0);
        graph.DroppedRootCount.ShouldBe(0);
        ((double)graph.EdgeCount / graph.ObjectCount).ShouldBeGreaterThan(2.5, "a dense heap, unlike the 1.1 to 1.2 of the leak fixtures");
        ((double)graph.ColumnBytes / graph.ObjectCount).ShouldBeInRange(30, 38, "the 6.2 budget for 2 to 4 references per object");
        GraphQueries.CountTypes(graph).Keys.ShouldContain(k => k.StartsWith("Microsoft.CodeAnalysis.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_fixture_is_killed_on_dispose_and_exits_by_itself_when_its_parent_exits()
    {
        var fixture = await FixtureProcess.StartDenseAsync(1);
        var pid = fixture.ProcessId;
        fixture.Dispose();
        await WaitForExitAsync(pid);

        using var parent = ChildProcess.Start("cmd.exe", "/c", "ping -n 120 127.0.0.1 > nul");
        using var orphan = ChildProcess.Start(
            TestPaths.DenseHeapExe, "--target-mb", "1", "--parent-pid", parent.Process.Id.ToString(CultureInfo.InvariantCulture));
        (await orphan.WaitForOutputAsync("READY", TimeSpan.FromSeconds(120))).ShouldBeTrue(orphan.Output);

        parent.Process.Kill(entireProcessTree: true);

        (await orphan.WaitForExitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue("DenseHeap must exit when its parent exits");
    }

    public void Dispose() => _folder.Dispose();

    private static async Task WaitForExitAsync(int pid)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (IsRunning(pid))
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, $"process {pid} still running");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
