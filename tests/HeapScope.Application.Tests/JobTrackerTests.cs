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

using HeapScope.Application.Jobs;
using HeapScope.Contracts;

namespace HeapScope.Application.Tests;

public class JobTrackerTests
{
    private readonly JobTracker _tracker = new(TimeProvider.System);

    [Fact]
    public async Task Completed_job_reports_result_and_status()
    {
        var added = new List<TrackedJob>();
        _tracker.JobAdded += (_, job) => added.Add(job);

        var job = _tracker.Start("answer", (_, _) => Task.FromResult(42));

        (await job.Result).ShouldBe(42);
        await job.Completion;
        job.Status.ShouldBe(JobStatus.Completed);
        added.ShouldHaveSingleItem().ShouldBeSameAs(job);
        _tracker.Jobs.ShouldContain(job);
    }

    [Fact]
    public async Task Every_job_logs_its_start_and_outcome_under_its_own_trace()
    {
        // Sprint 2.3.7 review H4, M1: opening, capturing and inspections leave a trail, and each job's trace id is the one
        // its worker requests carry.
        var logger = new RecordingLogger<JobTracker>();
        var tracker = new JobTracker(TimeProvider.System, logger);
        string? traceInside = null;

        var done = tracker.Start("Open app.dmp", (_, _) =>
        {
            traceInside = System.Diagnostics.Activity.Current?.TraceId.ToString();
            return Task.FromResult(1);
        });
        await done.Completion;
        var refused = tracker.Start("Run duplicate-strings", (_, _) => Task.FromException<int>(new Workers.WorkerOperationException(HeapScopeErrorCode.CapabilityNotSupported, "This snapshot does not support: string contents.", null)));
        await refused.Completion;

        // Observed here, so the finalizer never reports it to a test that counts unobserved task exceptions.
        await Should.ThrowAsync<Workers.WorkerOperationException>(() => refused.Result);
        traceInside.ShouldNotBeNull();
        System.Diagnostics.Activity.Current.ShouldBeNull("the job's activity does not leak to the caller");
        logger.Lines.ShouldContain($"Job {done.Id} started: Open app.dmp");
        logger.Lines.ShouldContain(l => l.StartsWith($"Job {done.Id} completed in ", StringComparison.Ordinal) && l.EndsWith(": Open app.dmp", StringComparison.Ordinal));
        logger.Entries.ShouldContain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information
            && e.Line.Contains($"Job {refused.Id} failed", StringComparison.Ordinal)
            && e.Line.Contains("(CapabilityNotSupported): Run duplicate-strings: This snapshot does not support: string contents.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Progress_updates_the_job_and_raises_changed()
    {
        var changes = 0;
        _tracker.JobChanged += (_, _) => Interlocked.Increment(ref changes);
        var release = new TaskCompletionSource();

        var job = _tracker.Start("progress", async (progress, _) =>
        {
            progress.Report(new JobProgress("Step", 5, 10));
            await release.Task;
            return 0;
        });

        job.Progress.ShouldBe(new JobProgress("Step", 5, 10));
        release.SetResult();
        await job.Completion;
        changes.ShouldBe(2);
    }

    [Fact]
    public async Task Cancel_ends_the_job_as_canceled()
    {
        var job = _tracker.Start("forever", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        });

        job.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => job.Result);
        await job.Completion;
        job.Status.ShouldBe(JobStatus.Canceled);
        job.Cancel();
    }

    [Fact]
    public async Task Failure_keeps_the_message_and_completion_does_not_fault()
    {
        var job = _tracker.Start<int>("broken", (_, _) => throw new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(() => job.Result);
        await job.Completion;
        job.Status.ShouldBe(JobStatus.Failed);
        job.Error.ShouldBe("boom");
    }

    [Fact]
    public async Task A_throwing_listener_leaves_a_completed_job_completed()
    {
        // Peer review P9: the outcome is final before listeners hear of it, and a listener's failure is only logged.
        var logger = new RecordingLogger<JobTracker>();
        var tracker = new JobTracker(TimeProvider.System, logger);
        tracker.JobAdded += (_, _) => throw new InvalidOperationException("added listener");
        tracker.JobChanged += (_, _) => throw new InvalidOperationException("changed listener");

        var job = tracker.Start("answer", (progress, _) =>
        {
            progress.Report(new JobProgress("half", 1, 2));
            return Task.FromResult(42);
        });

        (await job.Result).ShouldBe(42);
        await job.Completion;
        job.Status.ShouldBe(JobStatus.Completed);
        job.Error.ShouldBeNull();
        logger.Entries.Count(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Line.Contains("listener of", StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task A_throwing_listener_does_not_replace_a_jobs_failure()
    {
        var tracker = new JobTracker(TimeProvider.System);
        tracker.JobChanged += (_, _) => throw new InvalidOperationException("listener");

        var job = tracker.Start<int>("broken", (_, _) => throw new FormatException("boom"));

        await Should.ThrowAsync<FormatException>(() => job.Result);
        await job.Completion;
        job.Status.ShouldBe(JobStatus.Failed);
        job.Error.ShouldBe("boom");
    }

    [Fact]
    public async Task Progress_after_completion_is_ignored()
    {
        IProgress<JobProgress>? captured = null;
        var job = _tracker.Start("done", (progress, _) =>
        {
            captured = progress;
            return Task.FromResult(1);
        });
        await job.Completion;

        captured!.Report(new JobProgress("late", 1, 1));

        job.Progress.ShouldBeNull();
    }

    [Fact]
    public async Task Clear_finished_keeps_running_jobs()
    {
        var release = new TaskCompletionSource();
        var running = _tracker.Start("running", async (_, _) =>
        {
            await release.Task;
            return 0;
        });
        var done = _tracker.Start("done", (_, _) => Task.FromResult(0));
        await done.Completion;

        _tracker.ClearFinished();

        _tracker.Jobs.ShouldBe([running]);
        release.SetResult();
        await running.Completion;
    }
}
