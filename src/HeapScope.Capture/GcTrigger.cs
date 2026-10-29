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
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace HeapScope.Capture;

/// <summary>What a forced collection did: whether its end was seen, and the collection's number and time taken.</summary>
public sealed record ForcedGc(bool Completed, int GcNumber, TimeSpan Elapsed);

/// <summary>
/// Forces a full blocking garbage collection in a .NET process (sprint 3.4, decision 1): an EventPipe session with the
/// runtime's GC and GC heap collect keywords makes the runtime run an induced full collection, as a gcdump capture does,
/// without walking the heap. Needs a .NET 5 or later target with a diagnostics port.
/// </summary>
public static class GcTrigger
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const long GcKeyword = 0x1;
    private const long GcHeapCollectKeyword = 0x800000;

    /// <summary>
    /// Triggers the collection and waits until its end event arrives or <paramref name="timeout"/> passes (then
    /// <see cref="ForcedGc.Completed"/> is false: the target may still be collecting).
    /// </summary>
    public static async Task<ForcedGc> CollectAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        EventPipeSession session;
        try
        {
            session = await new DiagnosticsClient(processId)
                .StartEventPipeSessionAsync([new EventPipeProvider(RuntimeProvider, EventLevel.Informational, GcKeyword | GcHeapCollectKeyword)], requestRundown: false, circularBufferMB: 64, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ServerNotAvailableException or ServerErrorException or UnauthorizedAccessException)
        {
            throw TraceRecorder.Translate(processId, ex);
        }

        using (session)
        {
            var gcNumber = -1;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new EventPipeEventSource(session.EventStream);
            source.Clr.GCStart += (GCStartTraceData data) =>
            {
                if (gcNumber < 0 && data.Depth == 2 && data.Type != GCType.BackgroundGC)
                {
                    gcNumber = data.Count;
                }
            };
            source.Clr.GCStop += (GCEndTraceData data) =>
            {
                if (data.Count == gcNumber)
                {
                    completed.TrySetResult();
                }
            };
            var processing = Task.Run(() =>
            {
                try
                {
                    source.Process();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException)
                {
                    // The session ended under the reader (stopped, or the target exited).
                }
            },
            CancellationToken.None);
            var finished = await Task.WhenAny(completed.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false) == completed.Task;
            try
            {
                await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ServerNotAvailableException or ServerErrorException or EndOfStreamException or IOException)
            {
                // The target exited after the collection.
            }

            if (await Task.WhenAny(processing, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None)).ConfigureAwait(false) != processing)
            {
                session.Dispose();
            }

            source.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            return new ForcedGc(finished, gcNumber, stopwatch.Elapsed);
        }
    }
}
