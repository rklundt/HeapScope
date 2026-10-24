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
using HeapScope.Application.Sessions;
using HeapScope.Capture;
using HeapScope.Integration.Tests.Infrastructure;
using Microsoft.Diagnostics.Tracing;

namespace HeapScope.Integration.Tests;

/// <summary>Sprint 1.5: EventPipe traces of a real process, through the recorder, the service and the CLI.</summary>
[Trait("Category", "Integration")]
public sealed class TraceTests(RunningApi shared) : IClassFixture<RunningApi>, IDisposable
{
    // Provider ids as EventPipe writes them (Microsoft-DotNETCore-SampleProfiler, Microsoft-Windows-DotNETRuntimeRundown).
    private static readonly Guid s_sampleProfiler = new("3c530d44-97ae-513a-1e6d-783e8f8e03a9");
    private static readonly Guid s_rundown = new("a669021c-c450-4609-a035-5af59af4df18");

    private readonly TempFolder _root = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_cpu_trace_of_LeakyApi_is_written_whole_and_readable_with_samples_and_rundown()
    {
        var path = _root.File("cpu.nettrace");

        var recording = TraceRecorder.RecordAsync(shared.Api.ProcessId, path, TraceProfile.Cpu, TimeSpan.FromSeconds(2), Ct);
        await shared.Api.PostWorkAsync(3, Ct);
        var result = await recording;

        result.StoppedEarly.ShouldBeFalse();
        result.Path.ShouldBe(path);
        result.Bytes.ShouldBe(new FileInfo(path).Length);
        File.Exists(path + ".partial").ShouldBeFalse("the partial file is renamed when complete");
        var events = Read(path);
        events.GetValueOrDefault(s_sampleProfiler).ShouldBeGreaterThan(0, "CPU samples");
        events.GetValueOrDefault(s_rundown).ShouldBeGreaterThan(0, "the rundown that names methods");
    }

    [Fact]
    public async Task Canceling_stops_the_trace_early_and_keeps_a_readable_file()
    {
        var path = _root.File("canceled.nettrace");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();

        var result = await new TraceService().RecordAsync(shared.Api.ProcessId, path, TraceKind.Gc, TimeSpan.FromMinutes(5), cancel.Token);

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60));
        result.StoppedEarly.ShouldBeTrue();
        File.Exists(path).ShouldBeTrue();
        Read(path).Values.Sum().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task An_existing_file_is_refused_and_left_untouched()
    {
        var path = _root.File("exists.nettrace");
        await File.WriteAllTextAsync(path, "keep me", Ct);

        var ex = await Should.ThrowAsync<CaptureFailedException>(
            () => new TraceService().RecordAsync(shared.Api.ProcessId, path, TraceKind.Cpu, TimeSpan.FromSeconds(1), Ct));

        ex.Reason.ShouldBe(CaptureFailure.FileExists);
        (await File.ReadAllTextAsync(path, Ct)).ShouldBe("keep me");
    }

    [Fact]
    public async Task Cli_trace_writes_the_file_and_fails_for_a_missing_process()
    {
        var path = _root.File("cli.nettrace");
        using var cli = ChildProcess.Start(TestPaths.CliExe, "trace", "--pid", shared.Api.ProcessId.ToString(CultureInfo.InvariantCulture), "--duration", "1", "--out", path);
        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue(cli.Output);
        cli.Process.ExitCode.ShouldBe(0, cli.Output);
        cli.Output.ShouldContain("Trace written: " + path);
        cli.Output.ShouldContain("Keep it private");
        File.Exists(path).ShouldBeTrue();

        using var missing = ChildProcess.Start(TestPaths.CliExe, "trace", "--pid", "1", "--duration", "1", "--out", _root.File("none.nettrace"));
        (await missing.WaitForExitAsync(TimeSpan.FromSeconds(60))).ShouldBeTrue(missing.Output);
        missing.Process.ExitCode.ShouldBe(1, missing.Output);
        File.Exists(_root.File("none.nettrace")).ShouldBeFalse();
    }

    public void Dispose() => _root.Dispose();

    /// <summary>Events per provider, read back with TraceEvent (what PerfView uses).</summary>
    private static Dictionary<Guid, int> Read(string path)
    {
        var counts = new Dictionary<Guid, int>();
        using var source = new EventPipeEventSource(path);
        source.AllEvents += e => counts[e.ProviderGuid] = counts.GetValueOrDefault(e.ProviderGuid) + 1;
        source.Process();
        return counts;
    }
}
