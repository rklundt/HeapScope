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
