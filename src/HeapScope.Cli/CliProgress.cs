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
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;

namespace HeapScope.Cli;

/// <summary>Progress on stderr: of a tracked job (an open, a capture), and of a worker call's phases.</summary>
internal static partial class CliCommands
{
    /// <summary>Waits for a tracked job with its progress on stderr; Ctrl+C cancels it.</summary>
    private static async Task<T> AwaitJobAsync<T>(CliContext context, TrackedJob<T> job, CancellationToken cancellationToken)
    {
        using var progress = new ConsoleProgress(context.Get<JobTracker>(), job, context.Error);
        using var cancel = cancellationToken.Register(job.Cancel);
        return await job.Result.ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a snapshot with progress on stderr, after printing the open warning (such as a load that may not fit in
    /// memory); with <paramref name="failOnWarning"/>, a warning returns null instead of opening.
    /// </summary>
    private static async Task<SnapshotHandle?> OpenWithProgressAsync(
        CliContext context,
        string path,
        CancellationToken cancellationToken,
        bool rebuild = false,
        SnapshotMemoryMode mode = SnapshotMemoryMode.Auto,
        bool failOnWarning = false)
    {
        var session = context.Get<SnapshotSession>();
        if (await session.GetOpenWarningAsync(path, rebuild, cancellationToken).ConfigureAwait(false) is { } warning)
        {
            await context.Error.WriteLineAsync("Warning: " + warning).ConfigureAwait(false);
            if (failOnWarning)
            {
                return null;
            }
        }

        return await AwaitJobAsync(context, session.StartOpen(path, rebuild, mode), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a dump through the session with progress on stderr, or returns null after printing why not.</summary>
    private static async Task<SnapshotHandle?> OpenForQueryAsync(CliContext context, FileInfo file, CancellationToken cancellationToken)
    {
        if (!file.Exists)
        {
            await context.Error.WriteLineAsync($"Error: the file {file.FullName} does not exist.").ConfigureAwait(false);
            return null;
        }

        return await OpenWithProgressAsync(context, file.FullName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Prints a worker call's progress phases to stderr (sprint 2.3: computing dominators takes seconds), each phase once
    /// and its percentage at most every few seconds, so piped output stays readable.
    /// </summary>
    internal sealed class StderrProgress(TextWriter error) : IProgress<JobProgress>
    {
        private readonly Lock _gate = new();
        private DateTime _lastWrite = DateTime.MinValue;
        private string? _lastPhase;

        public void Report(JobProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if (value.Phase == _lastPhase && now - _lastWrite < TimeSpan.FromSeconds(2))
                {
                    return;
                }

                _lastWrite = now;
                _lastPhase = value.Phase;
                var percent = value.Total is > 0 ? string.Create(CultureInfo.InvariantCulture, $" {100.0 * value.Completed / value.Total.Value,5:N1}%") : string.Empty;
                error.WriteLine(value.Phase + percent);
            }
        }
    }

    /// <summary>Prints job progress to stderr, at most a few times per second.</summary>
    private sealed class ConsoleProgress : IDisposable
    {
        private readonly JobTracker _tracker;
        private readonly TrackedJob _job;
        private readonly TextWriter _error;
        private readonly object _gate = new();
        private DateTime _lastWrite = DateTime.MinValue;
        private string? _lastPhase;

        public ConsoleProgress(JobTracker tracker, TrackedJob job, TextWriter error)
        {
            _tracker = tracker;
            _job = job;
            _error = error;
            _tracker.JobChanged += OnJobChanged;
        }

        public void Dispose() => _tracker.JobChanged -= OnJobChanged;

        private void OnJobChanged(object? sender, TrackedJob job)
        {
            if (job.Id != _job.Id || job.Progress is not { } progress)
            {
                return;
            }

            lock (_gate)
            {
                // A new phase is always printed (it can carry a warning, such as the paused target); repeats are throttled.
                var now = DateTime.UtcNow;
                if (progress.Phase == _lastPhase && now - _lastWrite < TimeSpan.FromMilliseconds(250))
                {
                    return;
                }

                _lastWrite = now;
                _lastPhase = progress.Phase;
                var percent = progress.Total is > 0 ? $" {100.0 * progress.Completed / progress.Total.Value,5:N1}%" : string.Empty;
                _error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{progress.Phase}{percent}{(progress.Message is { } message ? ": " + message : string.Empty)}"));
            }
        }
    }
}
