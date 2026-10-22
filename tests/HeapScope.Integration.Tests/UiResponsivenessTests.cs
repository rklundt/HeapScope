using HeapScope.Integration.Tests.Infrastructure;

namespace HeapScope.Integration.Tests;

/// <summary>Runs alone: responsiveness measurements are meaningless while other tests saturate the CPU.</summary>
[CollectionDefinition(nameof(UiResponsivenessCollection), DisableParallelization = true)]
public sealed class UiResponsivenessCollection;

/// <summary>Sprint 0.3: no dispatcher block over 100 ms while a 5M synthetic job runs (decision 7).</summary>
[Collection(nameof(UiResponsivenessCollection))]
[Trait("Category", "Integration")]
[Trait("Category", "UI")]
public sealed class UiResponsivenessTests : IDisposable
{
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Ui_stays_responsive_during_a_5M_synthetic_job()
    {
        using var ui = ChildProcess.Start(TestPaths.UiExe, "--smoke-synthetic", "5000000", "--Logging:File:Directory", _logDirectory);

        (await ui.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue();
        var log = string.Join(Environment.NewLine, Directory.GetFiles(_logDirectory, "*.log").Select(File.ReadAllText));
        ui.Process.ExitCode.ShouldBe(0, log);
        log.ShouldContain("Smoke run: job completed True");
    }

    public void Dispose()
    {
        if (Directory.Exists(_logDirectory))
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
    }
}
