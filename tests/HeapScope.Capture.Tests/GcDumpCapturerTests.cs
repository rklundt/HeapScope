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

namespace HeapScope.Capture.Tests;

/// <summary>gcdump capture failure handling, cleanup and cancellation with a fake writer (sprint 1.8 review).</summary>
public sealed class GcDumpCapturerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly FakeElevation _elevation = new();
    private readonly FakeGcDumpWriter _writer = new();

    public GcDumpCapturerTests() => Directory.CreateDirectory(_folder);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GcDumpCapturer Capturer => new(_elevation, _writer);

    [Fact]
    public async Task A_successful_capture_renames_the_partial_file()
    {
        var path = File();

        await Capturer.CaptureAsync(Running(), path, null, null, Ct);

        System.IO.File.Exists(path).ShouldBeTrue();
        System.IO.File.Exists(path + DumpCapturer.PartialSuffix).ShouldBeFalse();
        _writer.Timeout.ShouldBe(GcDumpCapturer.DefaultTimeout);
    }

    [Fact]
    public async Task An_elevated_target_is_refused_before_the_runtime_is_asked()
    {
        await Should.ThrowAsync<ElevationRequiredException>(() => Capturer.CaptureAsync(Running(ElevationState.Elevated), File(), null, null, Ct));

        _writer.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(GcDumpFailure.Timeout, "did not finish reporting its heap within 30 seconds")]
    [InlineData(GcDumpFailure.NoHeap, "did not start reporting its heap")]
    [InlineData(GcDumpFailure.Error, "failed: boom")]
    public async Task A_failed_snapshot_deletes_the_partial_file_and_says_why(GcDumpFailure failure, string message)
    {
        _writer.Result = new GcDumpWriteResult(failure, "boom");
        var path = File();

        var ex = await Should.ThrowAsync<DumpWriteFailedException>(() => Capturer.CaptureAsync(Running(), path, null, null, Ct));

        ex.Message.ShouldContain(message);
        Directory.EnumerateFiles(_folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failure_of_a_process_that_is_gone_says_it_exited()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Capture runs on Windows only (ADR-025): elsewhere it refuses before the writer is called.");
        _writer.Result = new GcDumpWriteResult(GcDumpFailure.Error, "pipe closed");
        var gone = new DotNetProcess(int.MaxValue - 7, "Gone", null, "10.0.13", ElevationState.NotElevated, null, null, null);

        var ex = await Should.ThrowAsync<TargetUnavailableException>(() => Capturer.CaptureAsync(gone, File(), null, null, Ct));

        ex.Message.ShouldContain("exited");
    }

    [Fact]
    public async Task A_writer_that_cannot_write_is_reported_as_a_write_failure()
    {
        _writer.Throw = new UnauthorizedAccessException("denied");

        var ex = await Should.ThrowAsync<DumpWriteFailedException>(() => Capturer.CaptureAsync(Running(), File(), null, null, Ct));

        ex.Message.ShouldContain("denied");
        Directory.EnumerateFiles(_folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_success_without_a_file_is_a_write_failure()
    {
        _writer.WriteFile = false;

        await Should.ThrowAsync<DumpWriteFailedException>(() => Capturer.CaptureAsync(Running(), File(), null, null, Ct));
    }

    [Fact]
    public async Task An_existing_file_is_never_overwritten()
    {
        var path = File();
        await System.IO.File.WriteAllTextAsync(path, "mine", Ct);

        await Should.ThrowAsync<DumpFileExistsException>(() => Capturer.CaptureAsync(Running(), path, null, null, Ct));

        (await System.IO.File.ReadAllTextAsync(path, Ct)).ShouldBe("mine");
        _writer.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_cancellation_during_the_snapshot_removes_the_partial_file()
    {
        using var cancel = new CancellationTokenSource();
        _writer.OnWrite = cancel.Cancel;

        await Should.ThrowAsync<OperationCanceledException>(() => Capturer.CaptureAsync(Running(), File(), null, null, cancel.Token));

        Directory.EnumerateFiles(_folder).ShouldBeEmpty();
    }

    [Fact]
    public void The_vendored_log_tells_errors_timeouts_and_missing_heaps_apart()
    {
        EventPipeGcDumpWriter.Classify(["  0.1s: Requesting a .NET Heap Dump", "  2.0s: [Error] Exception during gcdump: System.IO.EndOfStreamException: Attempted to read past the end", "   at X.Y()"])
            .ShouldBe(new GcDumpWriteResult(GcDumpFailure.Error, "System.IO.EndOfStreamException: Attempted to read past the end"));
        EventPipeGcDumpWriter.Classify(["  5.1s: Assume no .NET Heap", "[  5.2s: Done Dumping .NET heap success=False]"]).Failure.ShouldBe(GcDumpFailure.NoHeap);
        EventPipeGcDumpWriter.Classify([" 30.1s: Timed out after 30 seconds"]).Failure.ShouldBe(GcDumpFailure.Timeout);
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

    // This test process stands in for a running target, so a failure is not mistaken for an exited one.
    private static DotNetProcess Running(ElevationState elevation = ElevationState.NotElevated) =>
        new(Environment.ProcessId, "App", null, "10.0.13", elevation, null, null, null);

    private string File() => Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".gcdump");

    private sealed class FakeElevation : IElevationProbe
    {
        public bool IsCurrentProcessElevated => false;

        public ElevationState GetState(int processId) => ElevationState.NotElevated;
    }

    private sealed class FakeGcDumpWriter : IGcDumpWriter
    {
        public int Calls { get; private set; }

        public TimeSpan Timeout { get; private set; }

        public GcDumpWriteResult Result { get; set; } = GcDumpWriteResult.Succeeded;

        public Exception? Throw { get; set; }

        public bool WriteFile { get; set; } = true;

        public Action? OnWrite { get; set; }

        public async Task<GcDumpWriteResult> WriteAsync(int processId, string path, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            Timeout = timeout;
            if (WriteFile)
            {
                await System.IO.File.WriteAllTextAsync(path, "gcdump", CancellationToken.None);
            }

            OnWrite?.Invoke();
            if (Throw is not null)
            {
                throw Throw;
            }

            return Result;
        }
    }
}
