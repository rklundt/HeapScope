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
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using HeapScope.Core;

namespace HeapScope.Capture;

/// <summary>What a trace records.</summary>
public enum TraceProfile
{
    /// <summary>CPU samples with call stacks and the runtime's default events (dotnet-trace's cpu-sampling).</summary>
    Cpu = 0,

    /// <summary>Garbage collections, their pauses and allocation ticks (sampled allocations).</summary>
    Gc = 1,

    /// <summary>
    /// Sampled allocations with call stacks (sprint 2.5): allocation ticks, one about every 100 KB allocated, plus the
    /// loader and JIT events and the rundown that name the methods on the stacks.
    /// </summary>
    Allocations = 2,
}

/// <summary>A trace that was written.</summary>
/// <param name="StoppedEarly">True when it ended before the duration: canceled, or the process exited.</param>
public sealed record TraceRecording(string Path, long Bytes, TimeSpan Duration, bool StoppedEarly);

/// <summary>
/// Records an EventPipe trace of a .NET process through its diagnostics port (sprint 1.5): the same session dotnet-trace
/// starts, so nobody installs dotnet-trace. The file is a <c>.nettrace</c> that PerfView, Visual Studio or TraceEvent
/// read. It is written to <c>&lt;name&gt;.partial</c> and renamed when complete; an existing file is never overwritten.
/// Canceling stops the recording early and keeps what was recorded; a failure deletes the partial file.
/// </summary>
public static class TraceRecorder
{
    private const long RuntimeDefaultKeywords = 0x14C14FCCBD;
    private const long GcKeyword = 0x1;
    private const long LoaderKeyword = 0x8;
    private const long JitKeyword = 0x10;
    private const long SampleProfilerKeywords = unchecked((long)0xF00000000000);

    /// <summary>The EventPipe providers of a profile.</summary>
    public static IReadOnlyList<EventPipeProvider> Providers(TraceProfile profile) => profile switch
    {
        TraceProfile.Gc =>
        [
            new("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, GcKeyword),
        ],
        TraceProfile.Allocations =>
        [
            // Allocation ticks are verbose GC events; EventPipe records a stack with each one. Loader and JIT events name
            // the modules and the methods compiled while recording; the rundown on stop names the rest.
            new("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, GcKeyword | LoaderKeyword | JitKeyword),
        ],
        _ =>
        [
            new("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational, SampleProfilerKeywords),
            new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, RuntimeDefaultKeywords),
        ],
    };

    /// <exception cref="TargetUnavailableException">The process is gone or does not answer on its diagnostics port.</exception>
    /// <exception cref="ElevationRequiredException">The process belongs to another user or is elevated.</exception>
    /// <exception cref="DumpFileExistsException">The trace file exists already.</exception>
    public static async Task<TraceRecording> RecordAsync(int processId, string path, TraceProfile profile, TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            throw new DumpFileExistsException($"{fullPath} already exists; HeapScope never overwrites a file. Choose another file name.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var partial = fullPath + ".partial";
        var stopwatch = Stopwatch.StartNew();
        EventPipeSession session;
        try
        {
            session = await new DiagnosticsClient(processId)
                .StartEventPipeSessionAsync(Providers(profile), requestRundown: true, circularBufferMB: 256, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ServerNotAvailableException or ServerErrorException or UnauthorizedAccessException)
        {
            throw Translate(processId, ex);
        }

        var stoppedEarly = false;
        try
        {
            using (session)
            {
                var file = PrivateFiles.Open(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                await using (file.ConfigureAwait(false))
                {
                    // The stream ends when the session stops (with the rundown that names methods) or the process exits.
                    var copy = session.EventStream.CopyToAsync(file, CancellationToken.None);
                    var elapsed = Task.Delay(duration, cancellationToken);
                    var first = await Task.WhenAny(copy, elapsed).ConfigureAwait(false);
                    stoppedEarly = first == copy || cancellationToken.IsCancellationRequested;
                    if (first != copy)
                    {
                        try
                        {
                            await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is ServerNotAvailableException or ServerErrorException or EndOfStreamException or IOException)
                        {
                            // The process exited as the recording ended; what was recorded is kept.
                            stoppedEarly = true;
                        }
                    }

                    // A stopped session ends its stream after the rundown; one that never ends (the target hung) must not
                    // hang the caller: give up after a while and keep what arrived (sprint 1.5 review).
                    if (await Task.WhenAny(copy, Task.Delay(s_streamEndTimeout, CancellationToken.None)).ConfigureAwait(false) == copy)
                    {
                        await copy.ConfigureAwait(false);
                    }
                    else
                    {
                        stoppedEarly = true;
                        session.Dispose();
                        try
                        {
                            await copy.ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is ObjectDisposedException or IOException or OperationCanceledException)
                        {
                            // Disposing the session ended the copy; the trace so far is in the file.
                        }
                    }
                }
            }
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        File.Move(partial, fullPath, overwrite: false);
        return new TraceRecording(fullPath, new FileInfo(fullPath).Length, stopwatch.Elapsed, stoppedEarly);
    }

    /// <summary>How long a stopped session may take to end its stream (the rundown) before the recording stops waiting.</summary>
    private static readonly TimeSpan s_streamEndTimeout = TimeSpan.FromSeconds(60);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the original failure is what the caller needs to see.
        }
    }

    private static Exception Translate(int processId, Exception ex)
    {
        string name;
        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch (Exception lookup) when (lookup is ArgumentException or InvalidOperationException)
        {
            return new TargetUnavailableException($"No running process has id {processId}.", ex);
        }

        return ex is UnauthorizedAccessException
            ? ElevationRequiredException.For(processId, name, ex)
            : new TargetUnavailableException($"{name} ({processId}) did not answer on its diagnostics port: {ex.Message} Is it a .NET 6 or later process?", ex);
    }
}
