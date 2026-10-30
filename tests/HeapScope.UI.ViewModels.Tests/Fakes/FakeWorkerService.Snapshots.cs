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
using System.IO;
using HeapScope.Application.Workers;
using HeapScope.Contracts;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>The fake worker's Snapshots (<see cref="ISnapshotService"/>).</summary>
internal sealed partial class FakeWorkerService : ISnapshotService
{
    public Task<SnapshotProbe> ProbeSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new HeapScope.Application.Workers.WorkerOperationException(HeapScopeErrorCode.InvalidArgument, $"The file {path} does not exist.", null);
        }

        var cache = path + ".hsidx";
        var state = File.Exists(cache) ? SnapshotCacheState.Valid : SnapshotCacheState.Missing;
        return Task.FromResult(new SnapshotProbe(path, new FileInfo(path).Length, state, cache, null, false));
    }

    public Task<SnapshotDescriptor> OpenSnapshotAsync(OpenSnapshotRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (OpenFailure is { } failure)
        {
            return Task.FromException<SnapshotDescriptor>(failure);
        }

        if (RequiredDac is { } dac && !(request.TrustedDacs ?? []).Contains(dac.Sha256))
        {
            return Task.FromException<SnapshotDescriptor>(new HeapScope.Application.Workers.WorkerOperationException(
                HeapScopeErrorCode.DacNotTrusted, $"The DAC next to the dump is not trusted (SHA-256 {dac.Sha256}).", null) { DacTrust = dac });
        }

        Opened.Add(request);
        return Task.FromResult(new SnapshotDescriptor(
            Guid.NewGuid(), request.Path, request.Path.EndsWith(".gcdump", StringComparison.OrdinalIgnoreCase) ? SnapshotKindDto.GcDump : SnapshotKindDto.FullDump, SnapshotFeatures.ObjectGraph, "App", 42, "10.0.13", CapturedAt?.Invoke(request.Path),
            1000, 3000, 10, 5, 48_000, SnapshotMemoryMode.InMemory, false, request.Path + ".hsidx", 1.5, ["A note."], CacheIdentity,
            CaptureTimeFromFile?.Invoke(request.Path) ?? false));
    }

    public Task CloseSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        Closed.Add(snapshotId);
        return Task.CompletedTask;
    }

    public Task<ResultSetInfo> GetTypeSummaryAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        _sets[id] = [.. Rows];
        return Task.FromResult(new ResultSetInfo(id, Rows.Count, []));
    }
}
