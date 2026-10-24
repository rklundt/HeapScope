// HeapScope
// Copyright (C) 2026 Ray Klundt
// https://github.com/rklundt/HeapScope
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, version 3 only, with additional terms
// under Section 7 of that license.
//
// Additional terms, including attribution requirements, apply.
// See the ADDITIONAL-TERMS.md file distributed with this program.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program (see the LICENSE file). If not, see
// <https://www.gnu.org/licenses/>.
//
// Commercial licenses are available. See COMMERCIAL.md.

using System.Diagnostics;
using System.Globalization;
using HeapScope.Integration.Tests.Infrastructure;

namespace HeapScope.Integration.Tests;

[Trait("Category", "Integration")]
public class SkeletonTests
{
    private const string Attribution = "Powered by HeapScope by Ray Klundt (https://github.com/rklundt/HeapScope)";
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
        var lines = cli.Output.Trim().Split('\n', StringSplitOptions.TrimEntries);
        lines.ShouldBe([expected, Attribution]);
    }

    [Fact]
    public async Task Cli_help_ends_with_the_attribution_notice()
    {
        using var cli = ChildProcess.Start(TestPaths.CliExe, "summary", "--help");

        (await cli.WaitForExitAsync(s_timeout)).ShouldBeTrue();
        cli.Process.ExitCode.ShouldBe(0);
        cli.Output.Trim().ShouldEndWith(Attribution);
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

        (await worker.WaitForExitAsync(TimeSpan.FromSeconds(2))).ShouldBeTrue(worker.Output);
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
