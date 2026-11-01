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
    /// <summary>
    /// Bumped on any incompatible change to the worker's contracts or their DTOs. The history of every version, and the
    /// rule for when to bump, is in docs/architecture/process-model.md (ARCHITECTURE.md 4.4); it is kept there only.
    /// </summary>
    public const int Version = 21;

    /// <summary>Maximum rows returned by one <see cref="IWorkerService.GetRowsAsync"/> call.</summary>
    public const int MaxPageSize = 10_000;

    /// <summary>
    /// Prefix of worker pipe names; the rest is 24 random hex digits. Off Windows a pipe is a Unix domain socket in the
    /// temp folder, whose path macOS limits to 104 characters with a long temp folder, so the name stays short (sprint
    /// 2.3.5).
    /// </summary>
    public const string PipeNamePrefix = "HeapScope.W.";
}
