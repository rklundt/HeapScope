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

/// <summary>The contracts the worker serves on one connection, one per feature area (sprint 3.8, peer review P5).</summary>
public static class WorkerContracts
{
    /// <summary>Every worker contract; each is served by exactly one handler.</summary>
    public static IReadOnlyList<Type> All { get; } =
    [
        typeof(IWorkerControl),
        typeof(IWorkerTestHooks),
        typeof(ISnapshotService),
        typeof(IResultSetService),
        typeof(IHeapQueryService),
        typeof(IRetentionService),
        typeof(IInspectionService),
        typeof(IAllocationTraceService),
    ];
}
