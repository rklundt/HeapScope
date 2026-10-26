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

using System.IO;
using HeapScope.Application.Jobs;
using HeapScope.Application.Sessions;
using HeapScope.Contracts;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>Capture use cases for view model tests: a scripted process list and captures that write a small file.</summary>
internal sealed class FakeCaptureService(string sessionsRoot, JobTracker jobs) : ICaptureService
{
    public SessionLibrary Library { get; } = new(sessionsRoot);

    public bool IsHeapScopeElevated { get; set; }

    public string? UnavailableReason { get; set; }

    public List<ProcessEntry> Processes { get; } = [];

    /// <summary>When set, captures fail with it.</summary>
    public CaptureFailedException? Failure { get; set; }

    public Task<IReadOnlyList<ProcessEntry>> ListProcessesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProcessEntry>>([.. Processes.Select(p => p with { NeedsAdministrator = p.Elevation == ProcessElevation.Elevated && !IsHeapScopeElevated })]);

    /// <summary>The kinds of the captures started, in order (sprint 1.8).</summary>
    public List<SnapshotKindDto> Kinds { get; } = [];

    public TrackedJob<CaptureResult> StartCapture(int processId, string? outputPath = null, string? label = null, SnapshotSource source = SnapshotSource.Manual, SnapshotKindDto kind = SnapshotKindDto.FullDump) =>
        jobs.Start($"Capture pid {processId}", async (progress, ct) =>
        {
            Kinds.Add(kind);
            if (Failure is { } failure)
            {
                throw failure;
            }

            var process = Processes.Single(p => p.ProcessId == processId);
            if (process.Elevation == ProcessElevation.Elevated && !IsHeapScopeElevated)
            {
                throw new CaptureFailedException(CaptureFailure.NeedsAdministrator, "Run HeapScope as administrator and capture again.", null);
            }

            var path = outputPath ?? Path.Combine(Library.Current.Folder, $"{process.Name}-{processId}{(kind == SnapshotKindDto.GcDump ? ".gcdump" : ".dmp")}");
            await File.WriteAllBytesAsync(path, new byte[128], ct);
            var session = Library.AddToCurrent(new SessionSnapshot(Guid.NewGuid(), path, process.Name, processId, process.RuntimeVersion, DateTimeOffset.UtcNow, 128, Kind: kind, Label: label));
            return new CaptureResult(path, 128, process, session, Kind: kind);
        });
}
