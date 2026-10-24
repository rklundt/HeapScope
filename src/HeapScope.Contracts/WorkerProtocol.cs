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

/// <summary>Protocol constants shared by the worker and its clients.</summary>
public static class WorkerProtocol
{
    /// <summary>Bumped on any incompatible change to <see cref="IWorkerService"/> or its DTOs.</summary>
    /// Version 2 (sprint 1.2): snapshots (probe, open, close, type summary) and the snapshot error codes.
    /// Version 3 (sprint 1.3): type summary queries (grouping, filter, sort, heap kinds), new column kinds and the key flag.
    /// Version 4 (sprint 1.4): type diffs.
    /// Version 5 (sprint 1.5): instances, object details and references; hidden key columns; sort by column name.
    /// Version 6 (sprint 1.6): inspections (list and run), instance scopes and SameValueAs; progress on object and
    /// references calls. ARCHITECTURE.md 4.4 has the details.
    public const int Version = 6;

    /// <summary>Maximum rows returned by one <see cref="IWorkerService.GetRowsAsync"/> call.</summary>
    public const int MaxPageSize = 10_000;

    /// <summary>Prefix of worker pipe names; the rest is a GUID.</summary>
    public const string PipeNamePrefix = "HeapScope.Worker.";
}
