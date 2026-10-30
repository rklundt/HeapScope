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

using Microsoft.Diagnostics.NETCore.Client;

namespace HeapScope.Capture.Tests;

/// <summary>Capture preflight, failure handling and cancellation with fake elevation, disk and writer (sprint 1.1).</summary>
public sealed class DumpCapturerTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly FakeElevation _elevation = new();
    private readonly FakeDisk _disk = new() { Available = 100 * Gb };
    private readonly FakeWriter _writer = new();
    private long _estimate = 100L * 1024 * 1024;

    public DumpCapturerTests() => Directory.CreateDirectory(_folder);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DumpCapturer Capturer => new(_elevation, _disk, _writer, _ => _estimate);

    [Fact]
    public async Task An_elevated_target_from_an_unelevated_HeapScope_asks_to_run_as_administrator()
    {
        var ex = await Should.ThrowAsync<ElevationRequiredException>(() => Capturer.CaptureAsync(Process(ElevationState.Elevated), Dump(), null, Ct));

        ex.Message.ShouldContain("Run HeapScope as administrator");
        ex.Message.ShouldContain("pid 4242");
        _writer.Calls.ShouldBe(0, "refused before the runtime is asked");
    }

    [Fact]
    public async Task An_elevated_HeapScope_may_capture_an_elevated_target()
    {
        _elevation.Current = true;

        await Capturer.CaptureAsync(Process(ElevationState.Elevated), Dump(), null, Ct);

        _writer.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Too_little_disk_space_is_refused_before_capture_with_both_numbers_and_the_reserve()
    {
        _estimate = 4 * Gb;
        _disk.Available = 4 * Gb + (DumpCapturer.ReserveBytes / 2);

        var ex = await Should.ThrowAsync<InsufficientDiskSpaceException>(() => Capturer.CaptureAsync(Process(), Dump(), null, Ct));

        ex.NeededBytes.ShouldBe(4 * Gb);
        ex.Message.ShouldContain("keeps 1.0 GB free");
        _writer.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Unknown_free_space_does_not_block_the_capture()
    {
        _disk.Available = null;

        await Capturer.CaptureAsync(Process(), Dump(), null, Ct);

        _writer.Calls.ShouldBe(1);
    }

    [Fact]
    public void The_fallback_estimate_covers_images_and_errs_high()
    {
        DumpSizeEstimator.FromPrivateBytes(100L * 1024 * 1024).ShouldBeGreaterThan(450L * 1024 * 1024, "images and mapped files are not in private bytes");
        DumpSizeEstimator.FromPrivateBytes(null).ShouldBe(DumpSizeEstimator.FallbackImageBytes + DumpSizeEstimator.StreamBytes);
    }

    [Fact]
    public void The_measured_estimate_of_this_process_is_its_committed_readable_memory()
    {
        var self = new DotNetProcess(Environment.ProcessId, "self", null, null, ElevationState.NotElevated, null, null, null);
        using var current = System.Diagnostics.Process.GetCurrentProcess();

        var committed = DumpSizeEstimator.CommittedReadableBytes(Environment.ProcessId);

        committed.ShouldNotBeNull();
        committed.Value.ShouldBeGreaterThan(current.PrivateMemorySize64 / 2);
        // The process grows between the two walks, so allow some drift.
        Math.Abs(DumpSizeEstimator.Estimate(self) - (committed.Value + DumpSizeEstimator.StreamBytes)).ShouldBeLessThan(64L * 1024 * 1024);
    }

    [Fact]
    public async Task The_dump_is_written_to_a_partial_name_and_renamed_on_success()
    {
        var dump = Dump();

        await Capturer.CaptureAsync(Process(), dump, null, Ct);

        _writer.LastPath.ShouldBe(dump + DumpCapturer.PartialSuffix);
        File.Exists(dump).ShouldBeTrue();
        File.Exists(dump + DumpCapturer.PartialSuffix).ShouldBeFalse();
    }

    [Fact]
    public async Task Canceling_waits_for_the_runtime_then_removes_the_whole_dump()
    {
        using var cancellation = new CancellationTokenSource();
        var phases = new List<string>();
        _writer.Behavior = async path =>
        {
            // The runtime keeps writing after the cancel; the capturer must not abandon it.
            await cancellation.CancelAsync();
            await Task.Delay(100, Ct);
            await File.WriteAllTextAsync(path, "complete dump", Ct);
        };
        var dump = Dump();

        await Should.ThrowAsync<OperationCanceledException>(() => Capturer.CaptureAsync(Process(), dump, new SyncProgress(p => phases.Add(p.Phase)), cancellation.Token));

        File.Exists(dump).ShouldBeFalse();
        File.Exists(dump + DumpCapturer.PartialSuffix).ShouldBeFalse("the finished file is deleted once the runtime released it");
        phases.ShouldContain("Canceling");
    }

    [Fact]
    public async Task Writing_reports_that_the_target_is_paused()
    {
        var messages = new List<string?>();

        await Capturer.CaptureAsync(Process(), Dump(), new SyncProgress(p => messages.Add(p.Message)), Ct);

        messages.ShouldContain(m => m != null && m.Contains("is paused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Access_denied_from_the_runtime_becomes_the_administrator_message()
    {
        _writer.Behavior = _ => throw new UnauthorizedAccessException("Access is denied.");

        var ex = await Should.ThrowAsync<ElevationRequiredException>(() => Capturer.CaptureAsync(Process(ElevationState.Unknown), Dump(), null, Ct));

        ex.Message.ShouldContain("administrator");
    }

    [Fact]
    public async Task A_runtime_error_explains_that_the_target_writes_the_dump_under_its_own_account()
    {
        _writer.Behavior = _ => throw new ServerErrorException("Writing dump failed (HRESULT: 0x80070005)");

        var ex = await Should.ThrowAsync<DumpWriteFailedException>(() => Capturer.CaptureAsync(Process(), Dump(), null, Ct));

        ex.Message.ShouldContain("cannot write to");
        ex.Message.ShouldContain("--out");
    }

    [Fact]
    public async Task A_full_disk_during_the_write_is_named()
    {
        _writer.Behavior = _ => throw new ServerErrorException("Writing dump failed (HRESULT: 0x80070070)");

        var ex = await Should.ThrowAsync<DumpWriteFailedException>(() => Capturer.CaptureAsync(Process(), Dump(), null, Ct));

        ex.Message.ShouldContain("the disk is full");
    }

    [Fact]
    public async Task An_exited_target_gives_a_plain_message()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Capture runs on Windows only (ADR-025): elsewhere it refuses before the writer is called.");
        _writer.Behavior = _ => throw new ServerNotAvailableException("gone");

        var ex = await Should.ThrowAsync<TargetUnavailableException>(() => Capturer.CaptureAsync(Process(), Dump(), null, Ct));

        ex.Message.ShouldContain("may have exited");
    }

    [Fact]
    public async Task An_existing_file_is_never_overwritten()
    {
        var dump = Dump();
        await File.WriteAllTextAsync(dump, "keep", Ct);

        await Should.ThrowAsync<DumpFileExistsException>(() => Capturer.CaptureAsync(Process(), dump, null, Ct));

        (await File.ReadAllTextAsync(dump, Ct)).ShouldBe("keep");
        _writer.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_root_path_is_refused_with_a_message()
    {
        await Should.ThrowAsync<ArgumentException>(() => Capturer.CaptureAsync(Process(), Path.GetPathRoot(_folder)!, null, Ct));
    }

    [Fact]
    public void The_real_probe_answers_for_this_process()
    {
        WindowsElevationProbe.Instance.GetState(Environment.ProcessId)
            .ShouldBe(WindowsElevationProbe.Instance.IsCurrentProcessElevated ? ElevationState.Elevated : ElevationState.NotElevated);
    }

    [Fact]
    public void Volume_free_space_is_read_for_local_and_missing_folders()
    {
        VolumeDiskSpace.Instance.AvailableBytes(_folder).ShouldNotBeNull().ShouldBeGreaterThan(0);
        VolumeDiskSpace.Instance.AvailableBytes(Path.Combine(_folder, "does", "not", "exist")).ShouldBeNull();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static DotNetProcess Process(ElevationState elevation = ElevationState.NotElevated) =>
        new(4242, "App", "App.exe", "10.0.13", elevation, 1, 100L * 1024 * 1024, null);

    private string Dump() => Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".dmp");

    private sealed class SyncProgress(Action<CaptureProgress> report) : IProgress<CaptureProgress>
    {
        public void Report(CaptureProgress value) => report(value);
    }

    private sealed class FakeElevation : IElevationProbe
    {
        public bool Current { get; set; }

        public bool IsCurrentProcessElevated => Current;

        public ElevationState GetState(int processId) => ElevationState.NotElevated;
    }

    private sealed class FakeDisk : IDiskSpace
    {
        public long? Available { get; set; }

        public long? AvailableBytes(string directory) => Available;
    }

    private sealed class FakeWriter : IDumpWriter
    {
        public int Calls { get; private set; }

        public string? LastPath { get; private set; }

        public Func<string, Task> Behavior { get; set; } = path => File.WriteAllTextAsync(path, "MDMP");

        public Task WriteAsync(int processId, string path)
        {
            Calls++;
            LastPath = path;
            return Behavior(path);
        }
    }
}
