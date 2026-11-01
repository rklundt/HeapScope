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

using System.Globalization;
using HeapScope.Application.Jobs;
using HeapScope.Application.Sessions;
using HeapScope.Capture;
using HeapScope.Integration.Tests.Infrastructure;
using HeapScope.Loaders.ClrMd;

namespace HeapScope.Integration.Tests;

/// <summary>A running LeakyApi shared by the capture tests.</summary>
public sealed class RunningApi : IAsyncLifetime
{
    public FixtureProcess Api { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Api = await FixtureProcess.StartApiAsync();

    public ValueTask DisposeAsync()
    {
        Api?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Sprint 1.1: discovery, capture, the session library and elevation against real processes.</summary>
[Trait("Category", "Integration")]
public sealed class CaptureTests(RunningApi shared) : IClassFixture<RunningApi>, IDisposable
{
    private readonly TempFolder _root = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Discovery_lists_LeakyApi_with_runtime_version_command_line_and_elevation()
    {
        var process = new ProcessDiscovery().List(Ct).SingleOrDefault(p => p.ProcessId == shared.Api.ProcessId);

        process.ShouldNotBeNull("LeakyApi must be listed");
        process.Name.ShouldBe("LeakyApi");
        process.RuntimeVersion.ShouldNotBeNull().ShouldStartWith("10.");
        process.CommandLine.ShouldNotBeNull().ShouldContain("LeakyApi");
        // A child of the test run shares its elevation (CI's runners run elevated, sprint 3.6).
        process.Elevation.ShouldBe(WindowsElevationProbe.Instance.IsCurrentProcessElevated ? ElevationState.Elevated : ElevationState.NotElevated);
        process.WorkingSetBytes.ShouldNotBeNull().ShouldBeGreaterThan(0);
        new ProcessDiscovery().List(Ct).ShouldNotContain(p => p.ProcessId == Environment.ProcessId, "HeapScope's own processes are not listed");
    }

    [Fact]
    public async Task Capturing_LeakyApi_writes_a_full_dump_into_the_session_and_records_it()
    {
        var service = Service();

        var result = await service.StartCapture(shared.Api.ProcessId).Result;

        var session = result.Session.ShouldNotBeNull();
        Path.GetDirectoryName(result.Path).ShouldBe(session.Folder);
        session.Folder.ShouldStartWith(_root.Path);
        File.Exists(Path.Combine(session.Folder, "dac", result.Process.RuntimeVersion!, "mscordaccore.dll"))
            .ShouldBeTrue("the target's DAC is kept next to the dump, per runtime version");
        File.Exists(result.Path).ShouldBeTrue();
        result.SizeBytes.ShouldBeGreaterThan(10L * 1024 * 1024);
        MinidumpHeader.Read(result.Path).HasHeapMemory.ShouldBeTrue("a full dump");
        var recorded = SessionLibrary.TryRead(session.Folder)!.Document.Snapshots.ShouldHaveSingleItem();
        recorded.ProcessId.ShouldBe(shared.Api.ProcessId);
        recorded.ProcessName.ShouldBe("LeakyApi");
        recorded.RuntimeVersion.ShouldNotBeNull();
        recorded.RuntimeDirectory.ShouldNotBeNull();
        result.SecretsNote.ShouldContain("never uploads");
        service.Library.ListRecent().ShouldHaveSingleItem().SnapshotCount.ShouldBe(1);
    }

    [Fact]
    public async Task An_elevated_target_gives_the_administrator_message_through_the_service()
    {
        var probe = new AlwaysElevatedTarget();
        var service = new CaptureService(new ProcessDiscovery(probe), new DumpCapturer(probe), new SessionLibrary(_root.Path), new JobTracker(TimeProvider.System), probe);

        var ex = await Should.ThrowAsync<CaptureFailedException>(() => service.StartCapture(shared.Api.ProcessId).Result);

        ex.Reason.ShouldBe(CaptureFailure.NeedsAdministrator);
        ex.Message.ShouldContain("Run HeapScope as administrator");
        (await service.ListProcessesAsync(Ct)).Single(p => p.ProcessId == shared.Api.ProcessId).NeedsAdministrator.ShouldBeTrue();
        Directory.EnumerateFiles(_root.Path, "*.dmp", SearchOption.AllDirectories).ShouldBeEmpty();
        Directory.EnumerateDirectories(_root.Path).ShouldBeEmpty("a refused capture leaves no empty session");
    }

    [Fact]
    public async Task Canceling_a_real_capture_leaves_no_dump_behind()
    {
        await shared.Api.PostWorkAsync(100, Ct);
        var service = Service();
        var job = service.StartCapture(shared.Api.ProcessId);
        await WaitUntilAsync(() => job.Progress?.Phase == "Writing dump" || job.Result.IsCompleted);
        job.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => job.Result);

        // The runtime finished writing before the job ended; the file is gone (or goes within the retry window).
        await WaitUntilAsync(() => !Directory.EnumerateFiles(_root.Path, "*.dmp*", SearchOption.AllDirectories).Any(), TimeSpan.FromSeconds(30));
        Directory.EnumerateDirectories(_root.Path).ShouldBeEmpty("the session created for the canceled capture is removed");
    }

    [Fact]
    public async Task A_captured_dump_opens_with_the_loader_from_its_session_folder_alone()
    {
        var trusted = new HeapScope.Application.Snapshots.TrustedDacs(_root.Path);
        var service = new CaptureService(new ProcessDiscovery(), new DumpCapturer(), new SessionLibrary(_root.Path), new JobTracker(TimeProvider.System), trustedDacs: trusted);
        var result = await service.StartCapture(shared.Api.ProcessId).Result;

        // Only the dump's own folder (where the DAC was copied) may be searched: the dump stays readable after the
        // runtime on this machine changes. The capture trusted the DAC it copied (sprint 3.6), so it loads without asking,
        // unless HeapScope runs elevated (CI's runners do): the target's runtime folder may then belong to a less
        // privileged user, and the open asks instead (review).
        if (WindowsElevationProbe.Instance.IsCurrentProcessElevated)
        {
            trusted.Kept().ShouldBeEmpty();
            Should.Throw<UntrustedDacException>(() => new ClrMdDumpLoader(new ClrMdLoaderOptions { InstalledRuntimeRoots = [], SearchDumpedRuntimeDirectory = false, IsTrustedDac = trusted.All().Contains })
                .Load(result.Path, cancellationToken: Ct));
            return;
        }

        var copied = trusted.Kept().ShouldHaveSingleItem();
        copied.Path.ShouldStartWith(Path.GetDirectoryName(result.Path)!);
        var options = new ClrMdLoaderOptions
        {
            InstalledRuntimeRoots = [],
            SearchDumpedRuntimeDirectory = false,
            IsTrustedDac = trusted.All().Contains,
            TrustedDacCopyFolder = Path.Combine(_root.Path, "dac-copies"),
        };
        var loaded = new ClrMdDumpLoader(options).Load(result.Path, cancellationToken: Ct);

        loaded.Graph.ObjectCount.ShouldBeGreaterThan(1000);
        loaded.Capabilities.HasFlag(Core.SnapshotCapabilities.CompleteGraph).ShouldBeTrue();
        loaded.Graph.Dispose();
    }

    [Fact]
    public async Task Quick_repeated_captures_get_distinct_files()
    {
        var service = Service();

        var first = await service.StartCapture(shared.Api.ProcessId).Result;
        var second = await service.StartCapture(shared.Api.ProcessId).Result;

        second.Path.ShouldNotBe(first.Path);
        second.Session!.Document.Snapshots.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_long_command_line_is_read_completely()
    {
        var longArgument = new string('x', 6000);
        using var fixture = await FixtureProcess.StartConsoleAsync("--leak", "StaticCache", "--count", "1", "--note", longArgument);

        var process = new ProcessDiscovery().Find(fixture.ProcessId);

        process.ShouldNotBeNull().CommandLine.ShouldNotBeNull().ShouldContain(longArgument);
    }

    [Fact]
    public async Task A_real_elevated_process_gives_the_administrator_message()
    {
        Assert.SkipWhen(WindowsElevationProbe.Instance.IsCurrentProcessElevated, "The test run is elevated, so every process can be dumped.");
        var elevated = new ProcessDiscovery().List(Ct).FirstOrDefault(p => p.Elevation == ElevationState.Elevated);
        Assert.SkipWhen(elevated is null, "No elevated .NET process is running on this machine.");

        var ex = await Should.ThrowAsync<CaptureFailedException>(() => Service().StartCapture(elevated!.ProcessId).Result);

        ex.Reason.ShouldBe(CaptureFailure.NeedsAdministrator);
        ex.Message.ShouldContain("administrator");
    }

    [Fact]
    public async Task A_process_that_is_not_dotnet_is_a_plain_error()
    {
        using var notDotNet = ChildProcess.Start("cmd.exe", "/c", "ping -n 30 127.0.0.1 > nul");

        var ex = await Should.ThrowAsync<CaptureFailedException>(() => Service().StartCapture(notDotNet.Process.Id).Result);

        ex.Reason.ShouldBe(CaptureFailure.ProcessNotFound);
        ex.Message.ShouldContain("No running .NET process");
    }

    [Fact]
    public async Task Cli_list_shows_LeakyApi_and_capture_writes_the_dump()
    {
        var output = _root.File("cli.dmp");
        using var list = ChildProcess.Start(TestPaths.CliExe, "list");
        (await list.WaitForExitAsync(TimeSpan.FromSeconds(60))).ShouldBeTrue(list.Output);
        list.Process.ExitCode.ShouldBe(0, list.Output);
        list.Output.ShouldContain(shared.Api.ProcessId.ToString(CultureInfo.InvariantCulture));
        list.Output.ShouldContain("LeakyApi");

        using var capture = ChildProcess.Start(TestPaths.CliExe, "capture", "--pid", shared.Api.ProcessId.ToString(CultureInfo.InvariantCulture), "--out", output);
        (await capture.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue(capture.Output);
        capture.Process.ExitCode.ShouldBe(0, capture.Output);
        capture.Output.ShouldContain("Captured LeakyApi");
        File.Exists(output).ShouldBeTrue();
    }

    [Fact]
    public async Task A_half_written_dump_left_by_a_killed_run_is_swept()
    {
        var library = new SessionLibrary(_root.Path);
        var folder = library.CreateSession("killed").Folder;
        var orphan = Path.Combine(folder, "app.dmp.partial");
        await File.WriteAllBytesAsync(orphan, new byte[10], Ct);
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-1));
        var fresh = Path.Combine(folder, "other.dmp.partial");
        await File.WriteAllBytesAsync(fresh, new byte[10], Ct);

        _ = Service();

        // The startup sweep runs in the background.
        await WaitUntilAsync(() => !File.Exists(orphan), TimeSpan.FromSeconds(10));
        File.Exists(orphan).ShouldBeFalse("an old partial dump is removed when capture starts up");
        File.Exists(fresh).ShouldBeTrue("a partial dump being written right now is left alone");
    }

    [Fact]
    public async Task Cli_capture_of_a_missing_process_fails()
    {
        using var capture = ChildProcess.Start(TestPaths.CliExe, "capture", "--pid", "1", "--out", _root.File("none.dmp"));

        (await capture.WaitForExitAsync(TimeSpan.FromSeconds(60))).ShouldBeTrue(capture.Output);
        capture.Process.ExitCode.ShouldBe(1, capture.Output);
        capture.Output.ShouldContain("No running .NET process");
    }

    [Fact]
    public async Task Cli_trust_dac_takes_a_SHA_256()
    {
        // Sprint 3.6 (ADR-035): a hash trusts a DAC for the run (anything else is a usage error, tested in HeapScope.Cli.Tests).
        using var good = ChildProcess.Start(TestPaths.CliExe, "--trust-dac", new string('A', 64), "worker", "ping");
        (await good.WaitForExitAsync(TimeSpan.FromSeconds(60))).ShouldBeTrue(good.Output);
        good.Process.ExitCode.ShouldBe(0, good.Output);
    }

    public void Dispose() => _root.Dispose();

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "condition not met in time");
            await Task.Delay(20, Ct);
        }
    }

    private CaptureService Service() =>
        new(new ProcessDiscovery(), new DumpCapturer(), new SessionLibrary(_root.Path), new JobTracker(TimeProvider.System));

    /// <summary>Reports every other process as elevated and HeapScope as not elevated.</summary>
    private sealed class AlwaysElevatedTarget : IElevationProbe
    {
        public bool IsCurrentProcessElevated => false;

        public ElevationState GetState(int processId) => ElevationState.Elevated;
    }
}

/// <summary>Tests that load the machine heavily (gigabyte heaps and dumps) run alone, so they cannot slow others into timeouts.
/// Each class in it also carries the Run=Heavy trait, which CI uses to run them in a job of their own
/// (build/test.sh checks that every class has it).</summary>
[CollectionDefinition(nameof(HeavyTestsCollection), DisableParallelization = true)]
public sealed class HeavyTestsCollection;

/// <summary>Ctrl+C during a real, multi-second capture (sprint 1.1 review H1).</summary>
[Trait("Category", "Integration")]
[Trait("Category", "Slow")]
[Trait("Run", "Heavy")]
[Collection(nameof(HeavyTestsCollection))]
public sealed class CliCancelTests : IDisposable
{
    private readonly TempFolder _root = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cli_ctrl_c_during_a_capture_waits_for_the_runtime_and_leaves_no_dump()
    {
        // A heap large enough that writing the dump takes longer than System.CommandLine's default 2 s grace period.
        using var dense = await FixtureProcess.StartDenseAsync(1500);
        var output = _root.File("ctrl-c.dmp");
        // Launched like an interactive shell would (Ctrl+C enabled); the test host's children otherwise ignore Ctrl+C.
        using var cli = ChildProcess.Start(TestPaths.LoadGenExe, "--launch", TestPaths.CliExe, "capture", "--pid", dense.ProcessId.ToString(CultureInfo.InvariantCulture), "--out", output);
        (await cli.WaitForOutputAsync("Writing dump", TimeSpan.FromSeconds(60))).ShouldBeTrue(cli.Output);

        using (var ctrlC = ChildProcess.Start(TestPaths.LoadGenExe, "--ctrl-c", cli.Process.Id.ToString(CultureInfo.InvariantCulture)))
        {
            (await ctrlC.WaitForExitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue(ctrlC.Output);
            ctrlC.Process.ExitCode.ShouldBe(0, "the Ctrl+C was delivered");
        }

        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue(cli.Output);
        cli.Process.ExitCode.ShouldBe(130, cli.Output);
        cli.Output.ShouldContain("Canceling");
        Directory.EnumerateFiles(_root.Path, "*.dmp*").ShouldBeEmpty("the dump written after Ctrl+C was deleted before the CLI exited");
    }

    public void Dispose() => _root.Dispose();
}

