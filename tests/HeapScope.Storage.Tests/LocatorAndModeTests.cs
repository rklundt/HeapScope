namespace HeapScope.Storage.Tests;

public sealed class LocatorAndModeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));

    public LocatorAndModeTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Cache_goes_next_to_the_snapshot_when_the_folder_is_writable()
    {
        var snapshot = Path.Combine(_directory, "app.dmp");

        new IndexCacheLocator(Path.Combine(_directory, "fallback")).GetCachePath(snapshot).ShouldBe(snapshot + ".hsidx");
    }

    [Fact]
    public void Read_only_snapshot_folder_falls_back_to_the_cache_root()
    {
        // A "folder" that is really a file cannot accept new files.
        var notAFolder = Path.Combine(_directory, "file-not-folder");
        File.WriteAllText(notAFolder, "x");
        var fallback = Path.Combine(_directory, "fallback");
        var locator = new IndexCacheLocator(fallback);

        var path = locator.GetCachePath(Path.Combine(notAFolder, "app.dmp"));

        Path.GetDirectoryName(path).ShouldBe(fallback);
        path.ShouldEndWith(".hsidx");
        locator.GetCachePath(Path.Combine(notAFolder, "app.dmp")).ShouldBe(path, "the fallback name is stable");
        locator.GetCachePath(Path.Combine(notAFolder, "other.dmp")).ShouldNotBe(path);
    }

    [Fact]
    public void Injected_write_check_controls_the_choice()
    {
        var locator = new IndexCacheLocator(@"C:\cache-root", _ => false);

        locator.GetCachePath(@"D:\dumps\app.dmp").ShouldStartWith(@"C:\cache-root\");
        IndexCacheLocator.CanWriteDirectory(_directory).ShouldBeTrue();
        Directory.GetFiles(_directory).ShouldBeEmpty("the probe file is removed");
    }

    [Fact]
    public void Graph_opens_in_memory_when_it_fits_the_budget_and_mapped_otherwise()
    {
        var sixteenGb = 16L * 1024 * 1024 * 1024;
        var budget = new MemoryBudget(sixteenGb, AvailableBytes: 6L * 1024 * 1024 * 1024);

        budget.BudgetBytes.ShouldBe(6L * 1024 * 1024 * 1024, "capped by available memory");
        MemoryModeSelector.Select(2L * 1024 * 1024 * 1024, budget).ShouldBe(MemoryMode.InMemory);
        MemoryModeSelector.Select(7L * 1024 * 1024 * 1024, budget).ShouldBe(MemoryMode.Mapped);
        (budget with { AvailableBytes = sixteenGb }).BudgetBytes.ShouldBe(sixteenGb / 2, "half of physical memory");
        MemoryModeSelector.Select(1, budget with { Forced = MemoryMode.Mapped }).ShouldBe(MemoryMode.Mapped);
        var current = MemoryBudget.Current();
        current.TotalPhysicalBytes.ShouldBeGreaterThan(0);
        current.AvailableBytes.ShouldBeInRange(1, current.TotalPhysicalBytes, "live available memory, even before the first GC");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
