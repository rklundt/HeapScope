using HeapScope.Application.Snapshots;

namespace HeapScope.Application.Tests;

public sealed class SnapshotMemoryEstimateTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Estimate_covers_the_measured_sample_peaks()
    {
        // Sprint 1.2 measurements (load-dump): Small 568 MB dump peaked at 227 MB (1,049 MB before the memory-mapped reader), Medium 4,022 MB at 2,395 MB.
        SnapshotMemoryEstimate.FirstLoadPeakBytes(568L * 1024 * 1024).ShouldBeGreaterThan(1_049L * 1024 * 1024);
        SnapshotMemoryEstimate.FirstLoadPeakBytes(4_022L * 1024 * 1024).ShouldBeGreaterThan(2_395L * 1024 * 1024);
    }

    [Fact]
    public void No_warning_when_the_estimate_fits_in_available_memory()
    {
        SnapshotMemoryEstimate.Warn(4 * Gb, new PhysicalMemory(32 * Gb, 20 * Gb)).ShouldBeNull();
    }

    [Fact]
    public void Warning_names_the_need_and_what_is_available()
    {
        var warning = SnapshotMemoryEstimate.Warn(8 * Gb, new PhysicalMemory(16 * Gb, 3 * Gb));

        warning.ShouldNotBeNull();
        warning.ShouldContain("about 5.1 GB");
        warning.ShouldContain("only 3.0 GB of 16.0 GB");
    }

    [Fact]
    public void Live_physical_memory_is_plausible()
    {
        var memory = PhysicalMemory.Current();

        memory.TotalBytes.ShouldBeGreaterThan(Gb);
        memory.AvailableBytes.ShouldBeInRange(1, memory.TotalBytes);
    }
}
