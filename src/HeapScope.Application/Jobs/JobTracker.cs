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
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using HeapScope.Application.Events;

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
public sealed class JobTracker(TimeProvider timeProvider, ILogger<JobTracker>? logger = null)
{
    /// <summary>The activity each job runs under: its trace id is a scope of every log line the job causes, in this process and, through StreamJsonRpc, in the worker (sprint 2.3.7 review M1).</summary>
    public const string ActivityName = "HeapScope.Job";

    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

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

        SafeEvents.Raise(JobAdded, this, job, _logger, nameof(JobAdded));
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

    // Every job's start and end are logged with its id, title, time and outcome (sprint 2.3.7 review H4): opening, capturing
    // and running an inspection all go through here. The message shown to the user is logged as it was shown.
    private async Task<T> RunAsync<T>(TrackedJob<T> job, Func<IProgress<JobProgress>, CancellationToken, Task<T>> work, IProgress<JobProgress> progress)
    {
        // Started directly, not through an ActivitySource: there is no listener, and the trace id is all that is needed.
        using var activity = new Activity(ActivityName).SetTag("job.id", job.Id).SetTag("job.title", job.Title).Start();
        var started = Stopwatch.GetTimestamp();
        _logger.LogInformation("Job {JobId} started: {Title}", job.Id, job.Title);
        T result;
        try
        {
            result = await work(progress, job.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job.Finish(JobStatus.Canceled, null);
            _logger.LogInformation("Job {JobId} canceled after {Seconds:N2} s: {Title}", job.Id, Stopwatch.GetElapsedTime(started).TotalSeconds, job.Title);
            SafeEvents.Raise(JobChanged, this, job, _logger, nameof(JobChanged));
            throw;
        }
        catch (Exception ex)
        {
            job.Finish(JobStatus.Failed, ex.Message);
            if (ex is WorkerOperationException coded)
            {
                // A refusal or failure the worker explained; the worker logged its side with the same trace id.
                _logger.LogInformation("Job {JobId} failed after {Seconds:N2} s ({Code}): {Title}: {Message}", job.Id, Stopwatch.GetElapsedTime(started).TotalSeconds, coded.ErrorCode, job.Title, ex.Message);
            }
            else
            {
                _logger.LogWarning(ex, "Job {JobId} failed after {Seconds:N2} s: {Title}: {Message}", job.Id, Stopwatch.GetElapsedTime(started).TotalSeconds, job.Title, ex.Message);
            }

            SafeEvents.Raise(JobChanged, this, job, _logger, nameof(JobChanged));
            throw;
        }

        // The outcome is final before anyone hears of it: a listener that throws cannot turn a completed job into a
        // failed one (peer review P9).
        job.Finish(JobStatus.Completed, null);
        _logger.LogInformation("Job {JobId} completed in {Seconds:N2} s: {Title}", job.Id, Stopwatch.GetElapsedTime(started).TotalSeconds, job.Title);
        SafeEvents.Raise(JobChanged, this, job, _logger, nameof(JobChanged));
        return result;
    }

    private void OnProgress(TrackedJob job, JobProgress value)
    {
        if (job.SetProgress(value))
        {
            SafeEvents.Raise(JobChanged, this, job, _logger, nameof(JobChanged));
        }
    }

    private sealed class JobProgressSink(JobTracker tracker, TrackedJob job) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => tracker.OnProgress(job, value);
    }
}
