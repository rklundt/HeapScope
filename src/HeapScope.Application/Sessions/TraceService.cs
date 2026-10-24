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

using HeapScope.Capture;

namespace HeapScope.Application.Sessions;

/// <summary>What a trace records (front ends see this instead of HeapScope.Capture's type).</summary>
public enum TraceKind
{
    /// <summary>CPU samples with call stacks.</summary>
    Cpu = 0,

    /// <summary>Garbage collections, their pauses and sampled allocations.</summary>
    Gc = 1,
}

/// <summary>A trace that was written.</summary>
/// <param name="StoppedEarly">True when it ended before the duration: canceled, or the process exited.</param>
public sealed record TraceResult(string Path, long Bytes, TimeSpan Duration, bool StoppedEarly);

/// <summary>
/// Records EventPipe traces of .NET processes, HeapScope's own included (sprint 1.5): no dotnet-trace install. Failures
/// are <see cref="CaptureFailedException"/> with a plain-language next step, like captures.
/// </summary>
public sealed class TraceService
{
    /// <summary>Records <paramref name="processId"/> for <paramref name="duration"/> (or until canceled) into <paramref name="path"/>.</summary>
    public async Task<TraceResult> RecordAsync(int processId, string path, TraceKind kind, TimeSpan duration, CancellationToken cancellationToken)
    {
        try
        {
            var recording = await TraceRecorder.RecordAsync(processId, path, (TraceProfile)(int)kind, duration, cancellationToken).ConfigureAwait(false);
            return new TraceResult(recording.Path, recording.Bytes, recording.Duration, recording.StoppedEarly);
        }
        catch (Exception ex) when (Translate(ex) is { } failure)
        {
            throw failure;
        }
    }

    private static CaptureFailedException? Translate(Exception ex) => ex switch
    {
        ElevationRequiredException => new(CaptureFailure.NeedsAdministrator, ex.Message, ex),
        TargetUnavailableException => new(CaptureFailure.TargetExited, ex.Message, ex),
        DumpFileExistsException => new(CaptureFailure.FileExists, ex.Message, ex),
        IOException or UnauthorizedAccessException => new(CaptureFailure.WriteFailed, $"The trace could not be written: {ex.Message}", ex),
        ArgumentException => new(CaptureFailure.WriteFailed, ex.Message, ex),
        _ => null,
    };
}
