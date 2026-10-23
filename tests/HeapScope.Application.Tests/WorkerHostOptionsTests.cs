using HeapScope.Application.Workers;

namespace HeapScope.Application.Tests;

/// <summary>The worker's GC heap count leaves cores for the UI (sprint 1.3).</summary>
public sealed class WorkerHostOptionsTests
{
    [Fact]
    public void The_worker_gets_half_the_logical_processors_as_gc_heaps_unless_configured()
    {
        new WorkerHostOptions().ResolveGcHeapCount().ShouldBe(Math.Max(1, Environment.ProcessorCount / 2));
        new WorkerHostOptions { GcHeapCount = 3 }.ResolveGcHeapCount().ShouldBe(3);
        new WorkerHostOptions { GcHeapCount = 0 }.ResolveGcHeapCount().ShouldBe(1);
    }
}
