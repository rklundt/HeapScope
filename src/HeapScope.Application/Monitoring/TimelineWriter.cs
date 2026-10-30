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
using HeapScope.Core;
using Microsoft.Extensions.Logging;

namespace HeapScope.Application.Monitoring;

/// <summary>
/// A monitoring run's timeline CSV (sprint 1.7, split from the run in 3.8, peer review P11): created new in the run's
/// session folder, one row per sample, capture marker or event. Never creates a file after it is closed, and after its
/// first failure it writes nothing more and has warned once: the timeline is a convenience, and a full disk must not stop
/// monitoring or flood the log. Not thread-safe: the run calls it under its lock.
/// </summary>
internal sealed class TimelineWriter
{
    /// <summary>The header row; <see cref="TimelineFile"/> reads files that start with it.</summary>
    public const string Header = "time,gc_heap_bytes,gen0_bytes,gen1_bytes,gen2_bytes,loh_bytes,poh_bytes,gen0_gcs,gen1_gcs,gen2_gcs,allocated_bytes,time_in_gc_percent,working_set_bytes,event";

    private readonly ILogger _logger;
    private StreamWriter? _writer;
    private string? _path;
    private bool _failed;
    private bool _closed;

    private TimelineWriter(ILogger logger) => _logger = logger;

    /// <summary>The file, or null when none is written (no folder, or writing it failed).</summary>
    public string? Path => _failed ? null : _path;

    /// <summary>
    /// Creates <c>counters-&lt;pid&gt;-&lt;local time&gt;.csv</c> in <paramref name="folder"/>, with a numbered suffix when the
    /// name is taken (two runs on one process within a second); a timeline that cannot be created is logged and skipped.
    /// </summary>
    public static TimelineWriter Create(string? folder, int processId, DateTimeOffset localNow, ILogger logger, Guid runId)
    {
        var timeline = new TimelineWriter(logger);
        if (folder is null)
        {
            timeline._failed = true;
            return timeline;
        }

        try
        {
            var stem = string.Create(CultureInfo.InvariantCulture, $"counters-{processId}-{localNow:yyyyMMdd-HHmmss}");
            for (var n = 1; timeline._writer is null; n++)
            {
                var path = System.IO.Path.Combine(folder, n == 1 ? stem + ".csv" : string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}.csv"));
                try
                {
                    timeline._writer = new StreamWriter(PrivateFiles.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                    timeline._path = path;
                }
                catch (IOException) when (File.Exists(path) && n < 100)
                {
                    // Taken: try the next number.
                }
            }

            timeline._writer.WriteLine(Header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Monitoring run {RunId}: the timeline could not be created; the run goes on without it", runId);
            timeline._failed = true;
            timeline._writer?.Dispose();
            timeline._writer = null;
        }

        return timeline;
    }

    /// <summary>A timeline with no file: nothing is written (a run without a session folder, or one that writes none).</summary>
    public static TimelineWriter None(ILogger logger) => new(logger) { _failed = true };

    /// <summary>A row holding only an event (a forced collection), its text quoted for CSV.</summary>
    public void WriteEvent(DateTimeOffset at, string text) =>
        WriteLine(string.Create(CultureInfo.InvariantCulture, $"{at:O},,,,,,,,,,,,,\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));

    /// <summary>A sample's row.</summary>
    public void WriteSample(HeapSample s) =>
        WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{s.At:O},{s.GcHeapBytes},{s.Gen0Bytes},{s.Gen1Bytes},{s.Gen2Bytes},{s.LargeObjectHeapBytes},{s.PinnedObjectHeapBytes},{s.Gen0Collections},{s.Gen1Collections},{s.Gen2Collections},{s.AllocatedBytes},{s.TimeInGcPercent:0.##},{s.WorkingSetBytes},"));

    /// <summary>A capture marker: columns empty but the time and the event.</summary>
    public void WriteCapture(MonitorCapture capture)
    {
        var what = capture.Result is { } result ? System.IO.Path.GetFileName(result.Path) : "failed: " + capture.Error;
        var cell = "\"" + $"{MonitorRun.SnapshotEventPrefix}{capture.Rule}: {what}".Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        WriteLine(string.Create(CultureInfo.InvariantCulture, $"{capture.At:O},,,,,,,,,,,,,{cell}"));
    }

    /// <summary>
    /// Stops writing at once (the run calls it under its lock) and returns the file for the caller to close outside the
    /// lock with <see cref="CloseAsync(StreamWriter?)"/>.
    /// </summary>
    public StreamWriter? Detach()
    {
        _closed = true;
        var writer = _writer;
        _writer = null;
        return writer;
    }

    /// <summary>Closes a file <see cref="Detach"/> returned; a failure is logged.</summary>
    public async Task CloseAsync(StreamWriter? writer)
    {
        if (writer is null)
        {
            return;
        }

        try
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The monitoring timeline could not be closed");
        }
    }

    private void WriteLine(string line)
    {
        if (_closed || _failed || _writer is null)
        {
            return;
        }

        try
        {
            _writer.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The monitoring timeline could not be written; the run goes on without it");
            _failed = true;
            _writer.Dispose();
            _writer = null;
        }
    }
}
