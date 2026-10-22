using HeapScope.Contracts;

namespace HeapScope.Application.Jobs;

/// <summary>Status of a tracked job (ARCHITECTURE.md 4.5).</summary>
public enum JobStatus
{
    Running,
    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// One long-running operation shown in the UI's jobs panel or the CLI's progress line. State changes are raised by
/// <see cref="JobTracker"/> on whatever thread caused them; UI code marshals to its dispatcher.
/// </summary>
public class TrackedJob
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private JobStatus _status = JobStatus.Running;
    private JobProgress? _progress;
    private string? _error;

    internal TrackedJob(string title, DateTimeOffset startedAt)
    {
        Title = title;
        StartedAt = startedAt;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Title { get; }

    public DateTimeOffset StartedAt { get; }

    public JobStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public JobProgress? Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress;
            }
        }
    }

    /// <summary>User-facing error message when <see cref="Status"/> is <see cref="JobStatus.Failed"/>.</summary>
    public string? Error
    {
        get
        {
            lock (_gate)
            {
                return _error;
            }
        }
    }

    /// <summary>Completes when the job finishes in any state; never faults.</summary>
    public Task Completion { get; private protected set; } = Task.CompletedTask;

    internal CancellationToken CancellationToken => _cancellation.Token;

    /// <summary>Requests cancellation; the job ends as <see cref="JobStatus.Canceled"/> when the work honors it.</summary>
    public void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
    }

    internal bool SetProgress(JobProgress progress)
    {
        lock (_gate)
        {
            if (_status != JobStatus.Running)
            {
                return false;
            }

            _progress = progress;
            return true;
        }
    }

    internal void Finish(JobStatus status, string? error)
    {
        lock (_gate)
        {
            _status = status;
            _error = error;
        }

        _cancellation.Dispose();
    }
}

/// <summary>A tracked job with a typed result.</summary>
public sealed class TrackedJob<T> : TrackedJob
{
    internal TrackedJob(string title, DateTimeOffset startedAt)
        : base(title, startedAt)
    {
    }

    /// <summary>The job's result; faults or cancels like the underlying work.</summary>
    public Task<T> Result { get; private set; } = null!;

    internal void Attach(Task<T> result, Task completion)
    {
        Result = result;
        Completion = completion;
    }
}

/// <summary>Tracks long-running jobs, their progress and cancellation (ARCHITECTURE.md 4.5). UI-agnostic.</summary>
public sealed class JobTracker(TimeProvider timeProvider)
{
    private readonly object _gate = new();
    private readonly List<TrackedJob> _jobs = [];

    /// <summary>Raised on the starting thread when a job is added.</summary>
    public event EventHandler<TrackedJob>? JobAdded;

    /// <summary>Raised on any thread when a job's progress or status changes.</summary>
    public event EventHandler<TrackedJob>? JobChanged;

    public IReadOnlyList<TrackedJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs];
            }
        }
    }

    /// <summary>
    /// Starts <paramref name="work"/> as a tracked job. The work receives a progress sink that updates the job and a
    /// token that <see cref="TrackedJob.Cancel"/> triggers.
    /// </summary>
    public TrackedJob<T> Start<T>(string title, Func<IProgress<JobProgress>, CancellationToken, Task<T>> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(work);
        var job = new TrackedJob<T>(title, timeProvider.GetUtcNow());
        lock (_gate)
        {
            _jobs.Add(job);
        }

        JobAdded?.Invoke(this, job);
        var progress = new JobProgressSink(this, job);
        var result = RunAsync(job, work, progress);
        job.Attach(result, result.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        return job;
    }

    /// <summary>Removes finished jobs from the list.</summary>
    public void ClearFinished()
    {
        lock (_gate)
        {
            _jobs.RemoveAll(j => j.Status != JobStatus.Running);
        }
    }

    private async Task<T> RunAsync<T>(TrackedJob<T> job, Func<IProgress<JobProgress>, CancellationToken, Task<T>> work, IProgress<JobProgress> progress)
    {
        try
        {
            var result = await work(progress, job.CancellationToken).ConfigureAwait(false);
            job.Finish(JobStatus.Completed, null);
            JobChanged?.Invoke(this, job);
            return result;
        }
        catch (OperationCanceledException)
        {
            job.Finish(JobStatus.Canceled, null);
            JobChanged?.Invoke(this, job);
            throw;
        }
        catch (Exception ex)
        {
            job.Finish(JobStatus.Failed, ex.Message);
            JobChanged?.Invoke(this, job);
            throw;
        }
    }

    private void OnProgress(TrackedJob job, JobProgress value)
    {
        if (job.SetProgress(value))
        {
            JobChanged?.Invoke(this, job);
        }
    }

    private sealed class JobProgressSink(JobTracker tracker, TrackedJob job) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => tracker.OnProgress(job, value);
    }
}
