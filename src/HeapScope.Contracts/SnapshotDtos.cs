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

namespace HeapScope.Contracts;

// DTOs are records of primitives, strings, arrays and Contracts enums only (ARCHITECTURE.md 4.4, ADR-016).
// Opening snapshots.

/// <summary>How a snapshot's graph is held in the worker (ARCHITECTURE.md 6.5, ADR-015).</summary>
public enum SnapshotMemoryMode
{
    /// <summary>Chosen from the memory budget (requests only).</summary>
    Auto = 0,

    /// <summary>Columns are arrays in the worker's memory.</summary>
    InMemory = 1,

    /// <summary>Columns are views of the index cache file.</summary>
    Mapped = 2,
}

/// <summary>Parameters of <see cref="IWorkerService.OpenSnapshotAsync"/>.</summary>
/// <param name="Path">Full path of the dump.</param>
/// <param name="Rebuild">Ignore an existing index cache and load the dump again.</param>
/// <param name="Mode">Memory mode; <see cref="SnapshotMemoryMode.Auto"/> follows the memory budget.</param>
/// <param name="RequireCache">Open only from a valid index cache (lazy reopens after a worker restart).</param>
/// <param name="TrustedDacs">SHA-256 hashes (lower-case hex) of DACs the user trusts to load from next to a dump (sprint
/// 3.6, peer review P2); the worker keeps them for its lifetime.</param>
public sealed record OpenSnapshotRequest(string Path, bool Rebuild = false, SnapshotMemoryMode Mode = SnapshotMemoryMode.Auto, bool RequireCache = false, IReadOnlyList<string>? TrustedDacs = null);

/// <summary>
/// The error data of <see cref="HeapScopeErrorCode.DacNotTrusted"/>: the DAC found next to the dump, which the user may
/// trust by its hash (sprint 3.6).
/// </summary>
public sealed record DacTrustDetails(string Path, string Sha256, string RuntimeVersion);

/// <summary>State of a snapshot's index cache.</summary>
public enum SnapshotCacheState
{
    Missing = 0,
    Valid = 1,
    Invalid = 2,
}

/// <summary>What <see cref="IWorkerService.ProbeSnapshotAsync"/> found, before any load.</summary>
public sealed record SnapshotProbe(string Path, long FileBytes, SnapshotCacheState CacheState, string CachePath, string? CacheDetail, bool IsOpen);

/// <summary>A loaded snapshot. <see cref="Notes"/> are plain-language data quality notes for the user.</summary>
/// <param name="CacheIdentity">The identity of the index cache the snapshot was opened from or wrote (sprint 2.1, from the
/// 1.2 review); 0 when none was written. Optional indices next to the cache record it.</param>
/// <param name="CaptureTimeFromFile">True when <paramref name="CapturedAtUtc"/> is the file's write time, not a time the
/// snapshot recorded (sprint 2.3.6: Linux and macOS core dumps record none), so a copy of the file can change it.</param>
public sealed record SnapshotDescriptor(
    Guid SnapshotId,
    string Path,
    SnapshotKindDto Kind,
    SnapshotFeatures Features,
    string? ProcessName,
    int? ProcessId,
    string? RuntimeVersion,
    DateTimeOffset? CapturedAtUtc,
    int ObjectCount,
    long ReferenceCount,
    int TypeCount,
    int RootCount,
    ulong TotalSize,
    SnapshotMemoryMode Mode,
    bool FromCache,
    string? CachePath,
    double OpenSeconds,
    IReadOnlyList<string> Notes,
    ulong CacheIdentity = 0,
    bool CaptureTimeFromFile = false);
