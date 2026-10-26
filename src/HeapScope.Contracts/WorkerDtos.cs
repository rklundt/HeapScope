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
// The worker itself, jobs, and paged result sets.

/// <summary>Worker identity and resource state, returned by the handshake.</summary>
/// <remarks><see cref="InstanceId"/> changes whenever a new worker process starts, so clients can tell that worker-owned
/// ids (snapshots, result sets) from an earlier instance are gone.</remarks>
public sealed record WorkerInfo(
    Guid InstanceId,
    int ProtocolVersion,
    string ProductVersion,
    int ProcessId,
    bool ServerGc,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    long TotalAvailableMemoryBytes,
    bool TestHooksEnabled);

/// <summary>Progress of a long-running operation. <see cref="Total"/> is null when unknown.</summary>
public sealed record JobProgress(string Phase, long Completed, long? Total, string? Message = null);

/// <summary>Parameters of the synthetic diagnostic job.</summary>
public sealed record SyntheticJobRequest(int ObjectCount, double AverageReferences = 3, int Seed = 42, int TypeCount = 1000);

/// <summary>Outcome of the synthetic diagnostic job.</summary>
public sealed record SyntheticJobResult(
    int ObjectCount,
    int ReferenceCount,
    ulong TotalSize,
    long ColumnBytes,
    double ElapsedSeconds,
    ResultSetInfo TypeSummary);

/// <summary>Kind of the values in a result column; cells are sent as invariant-culture strings.</summary>
public enum ResultColumnKind
{
    Text = 0,
    Integer = 1,
    Bytes = 2,

    /// <summary>A percentage, 0 to 100, with up to two decimals.</summary>
    Percent = 3,

    /// <summary>An object address, as 0x-prefixed hexadecimal.</summary>
    Address = 4,

    /// <summary>A signed integer such as a delta (diffs, sprint 1.4).</summary>
    SignedInteger = 5,
}

/// <summary>One column of a result set. <see cref="IsKey"/> marks the column that names a row (for example the type).</summary>
/// <param name="Bound">
/// Whether the column's values are exact or lower bounds (sprint 2.4, 2.3.7 review M7): the retained bytes of a type,
/// namespace or assembly row are a lower bound (ADR-024); an object's, a dominator tree row's and a set's exact.
/// Front ends render from this one flag (<see cref="ValueBoundTexts"/>).
/// </param>
public sealed record ResultColumn(string Name, ResultColumnKind Kind, bool IsKey = false, ValueBound Bound = ValueBound.Exact);

/// <summary>Whether a column's values are exact (sprint 2.4).</summary>
public enum ValueBound
{
    Exact = 0,

    /// <summary>The true value is at least this.</summary>
    LowerBound = 1,
}

/// <summary>What a value bound means, said the same way by every front end (sprint 2.4, 2.3.7 review M7).</summary>
public static class ValueBoundTexts
{
    /// <summary>The note for a column of lower bounds, such as a group's retained bytes.</summary>
    public const string LowerBound = "At least this much: a group's retained bytes count only what its objects retain apart from each other; objects held by several of them together are not counted. \"Objects they retain\" computes the exact figure.";

    /// <summary>The marker a table puts on a lower bound.</summary>
    public const string Marker = "≥";
}

/// <summary>A result set held by the worker, fetched with <see cref="IWorkerService.GetRowsAsync"/>.</summary>
public sealed record ResultSetInfo(Guid Id, int TotalCount, IReadOnlyList<ResultColumn> Columns);

/// <summary>One page of a result set.</summary>
public sealed record ResultPage(Guid ResultSetId, int Offset, int TotalCount, IReadOnlyList<IReadOnlyList<string?>> Rows);
