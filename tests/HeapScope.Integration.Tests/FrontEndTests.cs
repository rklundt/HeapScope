using HeapScope.Integration.Tests.Infrastructure;

namespace HeapScope.Integration.Tests;

/// <summary>CLI and UI against a real worker (sprint 0.3).</summary>
[Trait("Category", "Integration")]
public sealed class FrontEndTests : IDisposable
{
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Cli_worker_ping_and_info_succeed()
    {
        using var ping = ChildProcess.Start(TestPaths.CliExe, "worker", "ping");
        (await ping.WaitForExitAsync(TimeSpan.FromSeconds(60))).ShouldBeTrue(ping.Output);
        ping.Process.ExitCode.ShouldBe(0, ping.Output);
        ping.Output.ShouldContain("responded in");

        using var info = ChildProcess.Start(TestPaths.CliExe, "worker", "info");
        (await info.WaitForExitAsync(TimeSpan.FromSeconds(60))).ShouldBeTrue(info.Output);
        info.Process.ExitCode.ShouldBe(0, info.Output);
        info.Output.ShouldContain("Server GC:         True");
    }

    [Fact]
    public async Task Cli_synthetic_prints_a_summary_and_top_types()
    {
        using var cli = ChildProcess.Start(TestPaths.CliExe, "synthetic", "--objects", "200000", "--top", "3");

        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(60))).ShouldBeTrue(cli.Output);
        cli.Process.ExitCode.ShouldBe(0, cli.Output);
        cli.Output.ShouldContain("200,000 objects, 600,000 references");
        cli.Output.ShouldContain("Top 3 of 1000 types");
    }

    [Fact]
    public async Task Cli_attach_to_missing_pipe_fails_with_a_clear_message()
    {
        using var cli = ChildProcess.Start(TestPaths.CliExe, "worker", "ping", "--pipe", "HeapScope.Missing." + Guid.NewGuid().ToString("N"));

        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(90))).ShouldBeTrue(cli.Output);
        cli.Process.ExitCode.ShouldBe(1, cli.Output);
        cli.Output.ShouldContain("did not open its pipe");
    }

    public void Dispose()
    {
        if (Directory.Exists(_logDirectory))
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
    }
}
