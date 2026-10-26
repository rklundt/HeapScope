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

using HeapScope.Contracts;

namespace HeapScope.Application.Sessions;

// Front ends see only Application and Contracts (ARCHITECTURE.md 3.1), so capture results and failures are expressed
// in these types rather than HeapScope.Capture's.

/// <summary>Whether a process can be dumped by this HeapScope process (ADR-014).</summary>
public enum ProcessElevation
{
    NotElevated = 0,
    Elevated = 1,
    Unknown = 2,
}

/// <summary>A running .NET process as front ends show it. Details that could not be read are null.</summary>
/// <param name="NeedsAdministrator">True when HeapScope must run as administrator to capture it.</param>
public sealed record ProcessEntry(
    int ProcessId,
    string Name,
    string? CommandLine,
    string? RuntimeVersion,
    ProcessElevation Elevation,
    bool NeedsAdministrator,
    long? WorkingSetBytes,
    long? PrivateBytes);

/// <summary>Why a capture failed.</summary>
public enum CaptureFailure
{
    /// <summary>Not classified (see the log).</summary>
    Unknown = 0,

    /// <summary>The target is elevated or another user's: run HeapScope as administrator.</summary>
    NeedsAdministrator = 1,

    /// <summary>The dump would not fit on the disk.</summary>
    InsufficientDiskSpace = 2,

    /// <summary>No running .NET process with that id.</summary>
    ProcessNotFound = 3,

    /// <summary>The process exited or stopped answering during the capture.</summary>
    TargetExited = 4,

    /// <summary>The dump file exists already; HeapScope never overwrites one.</summary>
    FileExists = 5,

    /// <summary>The runtime could not write the dump (disk full, the target's account cannot write the folder).</summary>
    WriteFailed = 6,

    /// <summary>Listing and capturing processes is not available on this system yet (Linux, macOS; ADR-025).</summary>
    NotSupportedOnThisSystem = 7,
}

/// <summary>The capture use cases front ends depend on (faked in their tests).</summary>
public interface ICaptureService
{
    /// <summary>The session library captures are recorded in.</summary>
    SessionLibrary Library { get; }

    /// <summary>True when this HeapScope process runs as administrator and can capture elevated processes.</summary>
    bool IsHeapScopeElevated { get; }

    /// <summary>
    /// Why listing and capturing processes is not available on this system, or null where it is (sprint 2.3.6): front ends
    /// say so instead of offering commands that fail.
    /// </summary>
    string? UnavailableReason { get; }

    /// <summary>The .NET processes HeapScope can see, read off the calling thread.</summary>
    Task<IReadOnlyList<ProcessEntry>> ListProcessesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Captures a full dump, or a gcdump when <paramref name="kind"/> says so (sprint 1.8), as a tracked job; fails with
    /// <see cref="CaptureFailedException"/>. <paramref name="source"/> records why (a rule of a monitoring run is
    /// <see cref="SnapshotSource.Timed"/>, sprint 1.7).
    /// </summary>
    Jobs.TrackedJob<CaptureResult> StartCapture(
        int processId,
        string? outputPath = null,
        string? label = null,
        SnapshotSource source = SnapshotSource.Manual,
        SnapshotKindDto kind = SnapshotKindDto.FullDump);
}

/// <summary>A capture failed; the message is plain language with a next step.</summary>
public sealed class CaptureFailedException : Exception
{
    public CaptureFailedException()
    {
    }

    public CaptureFailedException(string message)
        : base(message)
    {
    }

    public CaptureFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CaptureFailedException(CaptureFailure reason, string message, Exception? innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public CaptureFailure Reason { get; } = CaptureFailure.Unknown;
}
