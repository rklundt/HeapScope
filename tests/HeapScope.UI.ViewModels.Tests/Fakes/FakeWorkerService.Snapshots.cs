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
        var kind = request.Path.EndsWith(".gcdump", StringComparison.OrdinalIgnoreCase) ? SnapshotKindDto.GcDump : SnapshotKindDto.FullDump;
        var features = FeaturesOf(kind);
        return Task.FromResult(new SnapshotDescriptor(
            Guid.NewGuid(), request.Path, kind, features, "App", 42, "10.0.13", CapturedAt?.Invoke(request.Path),
            1000, 3000, 10, 5, 48_000, SnapshotMemoryMode.InMemory, false, request.Path + ".hsidx", 1.5, ["A note."], CacheIdentity,
            CaptureTimeFromFile?.Invoke(request.Path) ?? false, AvailabilityOf(kind, features)));
    }

    /// <summary>The features a snapshot of <paramref name="kind"/> reports, following the fake's flags.</summary>
    private SnapshotFeatures FeaturesOf(SnapshotKindDto kind)
    {
        var features = SnapshotFeatures.ObjectGraph | SnapshotFeatures.ObjectAddresses;
        features |= CompleteGraph ? SnapshotFeatures.CompleteGraph | SnapshotFeatures.ExactCounts : SnapshotFeatures.None;
        if (kind == SnapshotKindDto.FullDump)
        {
            features |= SnapshotFeatures.FieldValues | SnapshotFeatures.RootDetails;
            features |= HasGenerations ? SnapshotFeatures.Generations : SnapshotFeatures.None;
            features |= ValuesAvailable ? SnapshotFeatures.StringContents : SnapshotFeatures.None;
        }

        return features;
    }

    /// <summary>
    /// The availability list the worker's rules give for <paramref name="features"/>, with <see cref="Unavailable"/> on
    /// top, so a view model that keys on the list sees the same offers as one that keyed on the features.
    /// </summary>
    private List<FeatureAvailability> AvailabilityOf(SnapshotKindDto kind, SnapshotFeatures features)
    {
        string? Missing(SnapshotFeatures feature) => features.HasFlag(feature) ? null : CapabilityDescriptions.ExplainMissing(feature, kind);
        var listed = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SnapshotFeatureIds.UnreachableSet] = kind == SnapshotKindDto.GcDump
                ? "A gcdump holds only what survived its collection."
                : Missing(SnapshotFeatures.CompleteGraph),
            [SnapshotFeatureIds.InterfaceGrouping] = features.HasFlag(SnapshotFeatures.FieldValues) ? null : "Interfaces need a full dump.",
            [SnapshotFeatureIds.FieldValues] = Missing(SnapshotFeatures.FieldValues),
            [SnapshotFeatureIds.StringValues] = Missing(SnapshotFeatures.StringContents),
            [SnapshotFeatureIds.Generations] = Missing(SnapshotFeatures.Generations),
            [SnapshotFeatureIds.FinalizableScope] = kind == SnapshotKindDto.GcDump ? "Listing finalizable objects needs data a gcdump does not record." : null,
        };
        foreach (var (feature, why) in Unavailable)
        {
            listed[feature] = why;
        }

        return [.. listed.Select(f => new FeatureAvailability(f.Key, f.Value))];
    }

    public Task CloseSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        Closed.Add(snapshotId);
        return Task.CompletedTask;
    }
}
