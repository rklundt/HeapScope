using System.Diagnostics;
using System.Globalization;
using HeapScope.Integration.Tests.Infrastructure;

namespace HeapScope.Integration.Tests;

[Trait("Category", "Integration")]
public class SkeletonTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void Worker_is_deployed_beside_ui_cli_and_tests()
    {
        File.Exists(TestPaths.WorkerExe).ShouldBeTrue();
        File.Exists(Path.Combine(TestPaths.CliOutput, "worker", "HeapScope.Worker.exe")).ShouldBeTrue();
        File.Exists(Path.Combine(TestPaths.UiOutput, "worker", "HeapScope.Worker.exe")).ShouldBeTrue();
    }

    [Fact]
    public async Task Cli_version_prints_the_product_version()
    {
        using var cli = ChildProcess.Start(TestPaths.CliExe, "--version");

        (await cli.WaitForExitAsync(s_timeout)).ShouldBeTrue();
        cli.Process.ExitCode.ShouldBe(0);
        var productVersion = FileVersionInfo.GetVersionInfo(Path.Combine(TestPaths.CliOutput, "heapscope-cli.dll")).ProductVersion!;
        var expected = productVersion.Split('+')[0];
        cli.Output.Trim().ShouldBe(expected);
        cli.Output.ShouldNotContain("+");
    }

    [Fact]
    public async Task Worker_with_invalid_parent_pid_exits_with_usage_error()
    {
        using var worker = ChildProcess.Start(TestPaths.WorkerExe, "--pipe", "x", "--parent-pid", "abc");

        (await worker.WaitForExitAsync(s_timeout)).ShouldBeTrue();
        worker.Process.ExitCode.ShouldBe(2);
        worker.Output.ShouldContain("Invalid --parent-pid");
    }

    [Fact]
    public async Task Worker_exits_promptly_when_parent_is_not_running()
    {
        using var worker = ChildProcess.Start(TestPaths.WorkerExe, "--pipe", "x", "--parent-pid", int.MaxValue.ToString(CultureInfo.InvariantCulture));

        (await worker.WaitForExitAsync(s_timeout)).ShouldBeTrue(worker.Output);
        worker.Process.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Worker_without_pipe_exits_with_usage_error()
    {
        using var worker = ChildProcess.Start(TestPaths.WorkerExe);

        (await worker.WaitForExitAsync(s_timeout)).ShouldBeTrue();
        worker.Process.ExitCode.ShouldBe(2);
        worker.Output.ShouldContain("--pipe");
    }

    [Fact]
    public async Task Worker_exits_cleanly_when_its_parent_exits()
    {
        using var parent = ChildProcess.Start("cmd.exe", "/c", "ping -n 120 127.0.0.1 > nul");
        using var worker = ChildProcess.Start(
            TestPaths.WorkerExe, "--pipe", "HeapScope.Test." + Guid.NewGuid().ToString("N"), "--parent-pid", parent.Process.Id.ToString(CultureInfo.InvariantCulture));

        (await worker.WaitForOutputAsync("started", s_timeout)).ShouldBeTrue(worker.Output);
        parent.Process.Kill(entireProcessTree: true);

        (await worker.WaitForExitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue(worker.Output);
        worker.Process.ExitCode.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "UI")]
    public async Task Ui_shows_a_titled_shell_window_and_exits_cleanly()
    {
        using var ui = ChildProcess.Start(TestPaths.UiExe);

        var deadline = DateTime.UtcNow + s_timeout;
        while (DateTime.UtcNow < deadline && !ui.Process.HasExited)
        {
            ui.Process.Refresh();
            if (ui.Process.MainWindowHandle != IntPtr.Zero && ui.Process.MainWindowTitle.Length > 0)
            {
                break;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        ui.Process.MainWindowTitle.ShouldStartWith("HeapScope");
        ui.Process.CloseMainWindow().ShouldBeTrue();
        (await ui.WaitForExitAsync(s_timeout)).ShouldBeTrue();
        ui.Process.ExitCode.ShouldBe(0);
    }
}
