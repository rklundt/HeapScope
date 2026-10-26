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
using System.Text;
using Graphs;
using Microsoft.Diagnostics.Tools.GCDump;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.Capture;

/// <summary>Why a heap snapshot did not produce a gcdump.</summary>
public enum GcDumpFailure
{
    None = 0,

    /// <summary>The runtime did not finish reporting its heap within the timeout.</summary>
    Timeout = 1,

    /// <summary>The runtime reported nothing within a few seconds (no .NET heap, or the runtime is not ready).</summary>
    NoHeap = 2,

    /// <summary>The session failed: the target exited, refused the connection, or the events could not be read.</summary>
    Error = 3,

    /// <summary>The capture was canceled.</summary>
    Canceled = 4,

    /// <summary>
    /// The heap has more objects than the gcdump writer keeps (10 million nodes, review H3): the file would silently hold a
    /// part of the heap, so none is written.
    /// </summary>
    TooLarge = 5,
}

/// <summary>The outcome of <see cref="IGcDumpWriter.WriteAsync"/>.</summary>
/// <param name="Reason">The first line of the error, when <paramref name="Failure"/> is <see cref="GcDumpFailure.Error"/>.</param>
public sealed record GcDumpWriteResult(GcDumpFailure Failure, string? Reason = null)
{
    public static GcDumpWriteResult Succeeded { get; } = new(GcDumpFailure.None);

    public bool Success => Failure == GcDumpFailure.None;
}

/// <summary>Writes a gcdump of a process (faked in tests).</summary>
public interface IGcDumpWriter
{
    /// <summary>
    /// Takes a heap snapshot of <paramref name="processId"/> and writes it to <paramref name="path"/>. Returns why when the
    /// runtime did not deliver a complete heap snapshot; throws <see cref="OperationCanceledException"/> when canceled.
    /// </summary>
    Task<GcDumpWriteResult> WriteAsync(int processId, string path, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// The vendored dotnet-gcdump capture (THIRD-PARTY-NOTICES.md): an EventPipe session with the GC heap snapshot keywords,
/// which makes the runtime run one full blocking collection and report every live object and reference, written in
/// PerfView's gcdump format. Its progress lines (timings only) go to the log at Debug, its warnings and errors at Warning.
/// </summary>
public sealed class EventPipeGcDumpWriter(ILogger<EventPipeGcDumpWriter>? logger = null) : IGcDumpWriter
{
    /// <summary>How long a canceled capture may take to close its session before the capture stops waiting for it.</summary>
    public static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(10);

    /// <summary>What the vendored writer logs when it stops at its node cap.</summary>
    internal const string NodeCapWarning = "Exceeded max node count";

    private readonly ILogger _logger = logger ?? NullLogger<EventPipeGcDumpWriter>.Instance;

    public static EventPipeGcDumpWriter Instance { get; } = new();

    public async Task<GcDumpWriteResult> WriteAsync(int processId, string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var log = new LineLog();
        var work = Task.Run(
            () =>
            {
                var graph = new MemoryGraph(50_000);
                var seconds = (int)Math.Ceiling(timeout.TotalSeconds);
                if (!EventPipeDotNetHeapDumper.DumpFromEventPipe(cancellationToken, processId, null, graph, TextWriter.Synchronized(log), seconds, new DotNetHeapInfo()))
                {
                    return false;
                }

                graph.AllowReading();
                GCHeapDump.WriteMemoryGraph(graph, path, "HeapScope");
                return true;
            },
            CancellationToken.None);
        try
        {
            var success = await work.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var lines = log.Lines;
            if (success && lines.Any(l => l.Contains(NodeCapWarning, StringComparison.Ordinal)))
            {
                // The vendored writer stops at its node cap and still reports success (review H3).
                return new GcDumpWriteResult(GcDumpFailure.TooLarge);
            }

            return success ? GcDumpWriteResult.Succeeded : Classify(lines);
        }
        catch (OperationCanceledException)
        {
            // The vendored code ends its session when it sees the cancellation; wait a little for that, so the target is not
            // left with an open session, but never hang the caller on it (sprint 1.8 review H1).
            if (await Task.WhenAny(work, Task.Delay(CancelGrace, CancellationToken.None)).ConfigureAwait(false) != work)
            {
                _logger.LogWarning("The canceled gcdump session of pid {Pid} did not close within {Seconds} s", processId, CancelGrace.TotalSeconds);
            }

            throw;
        }
        finally
        {
            foreach (var line in log.Lines)
            {
                var problem = line.Contains("[Error]", StringComparison.Ordinal) || line.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Timed out", StringComparison.Ordinal) || line.Contains("Assume no", StringComparison.Ordinal);
                _logger.Log(problem ? LogLevel.Warning : LogLevel.Debug, "gcdump pid {Pid}: {Line}", processId, line);
            }
        }
    }

    /// <summary>Why the vendored capture failed, from the lines it wrote (it catches its own exceptions).</summary>
    internal static GcDumpWriteResult Classify(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var error = line.IndexOf("[Error]", StringComparison.Ordinal);
            if (error >= 0)
            {
                // "[Error] Exception during gcdump: <type>: <message>" followed by a stack trace on later lines.
                var text = line[(error + "[Error]".Length)..].Trim();
                const string Prefix = "Exception during gcdump:";
                return new GcDumpWriteResult(GcDumpFailure.Error, text.StartsWith(Prefix, StringComparison.Ordinal) ? text[Prefix.Length..].Trim() : text);
            }
        }

        return lines.Any(l => l.Contains("Assume no .NET Heap", StringComparison.Ordinal)) ? new GcDumpWriteResult(GcDumpFailure.NoHeap)
            : lines.Any(l => l.Contains("Timed out", StringComparison.Ordinal)) ? new GcDumpWriteResult(GcDumpFailure.Timeout)
            : new GcDumpWriteResult(GcDumpFailure.Error, "the heap snapshot did not complete");
    }

    /// <summary>Collects whole lines (the vendored code writes a few dozen per capture), keeping at most the last 200.</summary>
    private sealed class LineLog : TextWriter
    {
        private const int MaxLines = 200;
        private readonly Queue<string> _lines = new();
        private readonly StringBuilder _current = new();

        public override Encoding Encoding => Encoding.Unicode;

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public override void Write(char value)
        {
            lock (_lines)
            {
                if (value == '\n')
                {
                    _lines.Enqueue(_current.ToString().TrimEnd('\r'));
                    _current.Clear();
                    if (_lines.Count > MaxLines)
                    {
                        _lines.Dequeue();
                    }
                }
                else
                {
                    _current.Append(value);
                }
            }
        }
    }
}

/// <summary>
/// Captures a gcdump (sprint 1.8): the managed object graph only, a fraction of a full dump's size, with no field values
/// or string text. The target is not paused for a file write, but it runs one full blocking garbage collection, so
/// garbage is gone from the snapshot and the process's counters show an induced GC. Writes to a temporary name and
/// renames it only on success; a failed or canceled capture leaves no file.
/// </summary>
public sealed class GcDumpCapturer(IElevationProbe? elevation = null, IGcDumpWriter? writer = null)
{
    /// <summary>The usual file extension; files are told apart by content (Core's SnapshotFileSignature), not by name.</summary>
    public const string Extension = ".gcdump";

    /// <summary>How long the runtime may take to report the heap, like dotnet-gcdump's default.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly IElevationProbe _elevation = elevation ?? CapturePlatform.ElevationProbe();
    private readonly IGcDumpWriter _writer = writer ?? EventPipeGcDumpWriter.Instance;

    /// <summary>
    /// Captures a gcdump of <paramref name="process"/> to <paramref name="path"/>, waiting up to <paramref name="timeout"/>
    /// (<see cref="DefaultTimeout"/> when null) for the runtime. Throws <see cref="ElevationRequiredException"/>,
    /// <see cref="DumpFileExistsException"/>, <see cref="TargetUnavailableException"/>,
    /// <see cref="DumpWriteFailedException"/> or <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task CaptureAsync(DotNetProcess process, string path, IProgress<CaptureProgress>? progress, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrEmpty(path);
        CapturePlatform.EnsureSupported();
        progress?.Report(new CaptureProgress("Checking"));
        if (process.Elevation == ElevationState.Elevated && !_elevation.IsCurrentProcessElevated)
        {
            throw ElevationRequiredException.For(process.ProcessId, process.Name);
        }

        var file = PartialSnapshotFile.Reserve(path, "gcdump");
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new CaptureProgress("Collecting heap", null, $"{process.Name} runs a full garbage collection and reports its heap"));
        var limit = timeout ?? DefaultTimeout;
        GcDumpWriteResult result;
        try
        {
            result = await _writer.WriteAsync(process.ProcessId, file.PartialPath, limit, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            file.Discard();
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            file.Discard();
            throw new DumpWriteFailedException($"The gcdump could not be written to {file.Directory}: {ex.Message}", ex);
        }

        if (!result.Success)
        {
            file.Discard();
            throw Failure(process, limit, result);
        }

        file.Commit();
    }

    private static Exception Failure(DotNetProcess process, TimeSpan limit, GcDumpWriteResult result)
    {
        // The vendored session catches its own exceptions, so an exited target arrives here as a failure.
        if (!IsRunning(process.ProcessId))
        {
            return new TargetUnavailableException($"{process.Name} (pid {process.ProcessId}) exited while its heap was being reported.");
        }

        return result.Failure switch
        {
            GcDumpFailure.TooLarge => new DumpWriteFailedException(
                $"{process.Name} (pid {process.ProcessId}) has more objects than a gcdump can hold (10 million). Capture a full dump instead."),
            GcDumpFailure.NoHeap => new DumpWriteFailedException(
                $"{process.Name} (pid {process.ProcessId}) did not start reporting its heap within a few seconds. It may still be starting up; try again, or capture a full dump."),
            GcDumpFailure.Timeout => new DumpWriteFailedException(string.Create(
                CultureInfo.InvariantCulture,
                $"{process.Name} (pid {process.ProcessId}) did not finish reporting its heap within {limit.TotalSeconds:N0} seconds. A very large heap takes longer: try again, or capture a full dump.")),
            _ => new DumpWriteFailedException(
                $"The heap snapshot of {process.Name} (pid {process.ProcessId}) failed: {result.Reason ?? "no details"}. Try again, or capture a full dump."),
        };
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
